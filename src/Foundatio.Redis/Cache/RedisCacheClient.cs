﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Redis;
using Foundatio.Serializer;
using Foundatio.AsyncEx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Foundatio.Redis.Utility;

namespace Foundatio.Caching {
    public sealed class RedisCacheClient : ICacheClient, IHaveSerializer {
        private readonly RedisCacheClientOptions _options;
        private readonly ILogger _logger;

        private readonly AsyncLock _lock = new AsyncLock();
        private bool _scriptsLoaded;

        private LoadedLuaScript _removeByPrefix;
        private LoadedLuaScript _incrementWithExpire;
        private LoadedLuaScript _removeIfEqual;
        private LoadedLuaScript _replaceIfEqual;
        private LoadedLuaScript _setIfHigher;
        private LoadedLuaScript _setIfLower;

        public RedisCacheClient(RedisCacheClientOptions options) {
            _options = options;
            options.Serializer = options.Serializer ?? DefaultSerializer.Instance;
            _logger = options.LoggerFactory?.CreateLogger(typeof(RedisCacheClient)) ?? NullLogger.Instance;
            options.ConnectionMultiplexer.ConnectionRestored += ConnectionMultiplexerOnConnectionRestored;
        }

        public RedisCacheClient(Builder<RedisCacheClientOptionsBuilder, RedisCacheClientOptions> config)
            : this(config(new RedisCacheClientOptionsBuilder()).Build()) { }

        public Task<bool> RemoveAsync(string key) {
            return Database.KeyDeleteAsync(key);
        }

        public async Task<bool> RemoveIfEqualAsync<T>(string key, T expected) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            await LoadScriptsAsync().AnyContext();

            var expectedValue = expected.ToRedisValue(_options.Serializer);
            var redisResult = await Database.ScriptEvaluateAsync(_removeIfEqual, new { key = (RedisKey)key, expected = expectedValue }).AnyContext();
            var result = (int)redisResult;

            return result > 0;
        }

        public async Task<int> RemoveAllAsync(IEnumerable<string> keys = null) {
            if (keys == null) {
                var endpoints = _options.ConnectionMultiplexer.GetEndPoints();
                if (endpoints.Length == 0)
                    return 0;

                foreach (var endpoint in endpoints) {
                    var server = _options.ConnectionMultiplexer.GetServer(endpoint);
                    if (server.IsSlave)
                        continue;

                    try {
                        await server.FlushDatabaseAsync().AnyContext();
                        continue;
                    } catch (Exception) {}

                    try {
                        var redisKeys = server.Keys().ToArray();
                        if (redisKeys.Length > 0)
                            await Database.KeyDeleteAsync(redisKeys).AnyContext();
                    } catch (Exception) {}
                }
            } else {
                var redisKeys = keys.Where(k => !String.IsNullOrEmpty(k)).Select(k => (RedisKey)k).ToArray();
                if (redisKeys.Length > 0)
                    return (int)await Database.KeyDeleteAsync(redisKeys).AnyContext();
            }

            return 0;
        }

        public async Task<int> RemoveByPrefixAsync(string prefix) {
            await LoadScriptsAsync().AnyContext();

            try {
                var result = await Database.ScriptEvaluateAsync(_removeByPrefix, new { keys = prefix + "*" }).AnyContext();
                return (int)result;
            } catch (RedisServerException) {
                return 0;
            }
        }

        private static readonly RedisValue _nullValue = "@@NULL";

        public async Task<CacheValue<T>> GetAsync<T>(string key) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            var redisValue = await Database.StringGetAsync(key).AnyContext();
            return RedisValueToCacheValue<T>(redisValue);
        }

        private  CacheValue<ICollection<T>> RedisValuesToCacheValue<T>(RedisValue[] redisValues) {
            var result = new List<T>();
            foreach (var redisValue in redisValues) {
                if (!redisValue.HasValue)
                    continue;
                if (redisValue == _nullValue)
                    continue;

                try {
                    var value = redisValue.ToValueOfType<T>(_options.Serializer);
                    result.Add(value);
                } catch (Exception ex) {
                    if (_logger.IsEnabled(LogLevel.Error))
                        _logger.LogError(ex, "Unable to deserialize value {Value} to type {Type}", redisValue, typeof(T).FullName);
                }
            }

            return new CacheValue<ICollection<T>>(result, true);
        }

        private CacheValue<T> RedisValueToCacheValue<T>(RedisValue redisValue) {
            if (!redisValue.HasValue) return CacheValue<T>.NoValue;
            if (redisValue == _nullValue) return CacheValue<T>.Null;

            try {
                var value = redisValue.ToValueOfType<T>(_options.Serializer);
                return new CacheValue<T>(value, true);
            } catch (Exception ex) {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(ex, "Unable to deserialize value {Value} to type {Type}", redisValue, typeof(T).FullName);
                return CacheValue<T>.NoValue;
            }
        }

        public async Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys) {
            string[] keyArray = keys.ToArray();
            var values = await Database.StringGetAsync(keyArray.Select(k => (RedisKey)k).ToArray()).AnyContext();

            var result = new Dictionary<string, CacheValue<T>>();
            for (int i = 0; i < keyArray.Length; i++)
                result.Add(keyArray[i], RedisValueToCacheValue<T>(values[i]));

            return result;
        }

        public async Task<CacheValue<ICollection<T>>> GetSetAsync<T>(string key) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            var set = await Database.SetMembersAsync(key).AnyContext();
            return RedisValuesToCacheValue<T>(set);
        }

        public async Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (expiresIn?.Ticks < 0) {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Removing expired key: {Key}", key);

                await this.RemoveAsync(key).AnyContext();
                return false;
            }

            return await InternalSetAsync(key, value, expiresIn, When.NotExists).AnyContext();
        }

        public async Task<long> SetAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (values == null)
                throw new ArgumentNullException(nameof(values));

            if (expiresIn?.Ticks < 0) {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Removing expired key: {Key}", key);

                await this.RemoveAsync(key).AnyContext();
                return default;
            }

            var redisValues = new List<RedisValue>();
            foreach (var value in values.Distinct())
                redisValues.Add(value.ToRedisValue(_options.Serializer));

            long result = await Database.SetAddAsync(key, redisValues.ToArray()).AnyContext();
            if (result > 0 && expiresIn.HasValue)
                await SetExpirationAsync(key, expiresIn.Value).AnyContext();

            return result;
        }

        public async Task<long> SetRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (values == null)
                throw new ArgumentNullException(nameof(values));

            if (expiresIn?.Ticks < 0) {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Removing expired key: {Key}", key);

                await this.RemoveAsync(key).AnyContext();
                return default;
            }

            var redisValues = new List<RedisValue>();
            foreach (var value in values.Distinct())
                redisValues.Add(value.ToRedisValue(_options.Serializer));

            long result = await Database.SetRemoveAsync(key, redisValues.ToArray()).AnyContext();
            if (result > 0 && expiresIn.HasValue)
                await SetExpirationAsync(key, expiresIn.Value).AnyContext();

            return result;
        }

        public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            return InternalSetAsync(key, value, expiresIn);
        }

        public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            await LoadScriptsAsync().AnyContext();

            if (expiresIn.HasValue) {
                var result = await Database.ScriptEvaluateAsync(_setIfHigher, new { key = (RedisKey)key, value, expires = expiresIn.Value.TotalSeconds }).AnyContext();
                return (double)result;
            } else {
                var result = await Database.ScriptEvaluateAsync(_setIfHigher, new { key = (RedisKey)key, value, expires = RedisValue.EmptyString }).AnyContext();
                return (double)result;
            }
        }

        public async Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            await LoadScriptsAsync().AnyContext();

            if (expiresIn.HasValue) {
                var result = await Database.ScriptEvaluateAsync(_setIfHigher, new { key = (RedisKey)key, value, expires = expiresIn.Value.TotalSeconds }).AnyContext();
                return (long)result;
            } else {
                var result = await Database.ScriptEvaluateAsync(_setIfHigher, new { key = (RedisKey)key, value, expires = RedisValue.EmptyString }).AnyContext();
                return (long)result;
            }
        }

        public async Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            await LoadScriptsAsync().AnyContext();

            if (expiresIn.HasValue) {
                var result = await Database.ScriptEvaluateAsync(_setIfLower, new { key = (RedisKey)key, value, expires = expiresIn.Value.TotalSeconds }).AnyContext();
                return (double)result;
            } else {
                var result = await Database.ScriptEvaluateAsync(_setIfLower, new { key = (RedisKey)key, value, expires = RedisValue.EmptyString }).AnyContext();
                return (double)result;
            }
        }

        public async Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            await LoadScriptsAsync().AnyContext();

            if (expiresIn.HasValue) {
                var result = await Database.ScriptEvaluateAsync(_setIfLower, new { key = (RedisKey)key, value, expires = expiresIn.Value.TotalSeconds }).AnyContext();
                return (long)result;
            } else {
                var result = await Database.ScriptEvaluateAsync(_setIfLower, new { key = (RedisKey)key, value, expires = RedisValue.EmptyString }).AnyContext();
                return (long)result;
            }
        }

        private Task<bool> InternalSetAsync<T>(string key, T value, TimeSpan? expiresIn = null, When when = When.Always, CommandFlags flags = CommandFlags.None) {
            var redisValue = value.ToRedisValue(_options.Serializer);
            return Database.StringSetAsync(key, redisValue, expiresIn, when, flags);
        }

        public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null) {
            if (values == null || values.Count == 0)
                return 0;

            var tasks = new List<Task<bool>>();
            foreach (var pair in values)
                tasks.Add(Database.StringSetAsync(pair.Key, pair.Value.ToRedisValue(_options.Serializer), expiresIn));

            bool[] results = await Task.WhenAll(tasks).AnyContext();
            return results.Count(r => r);
        }

        public Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            return InternalSetAsync(key, value, expiresIn, When.Exists);
        }

        public async Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            await LoadScriptsAsync().AnyContext();

            var redisValue = value.ToRedisValue(_options.Serializer);
            var expectedValue = expected.ToRedisValue(_options.Serializer);
            RedisResult redisResult;
            if (expiresIn.HasValue)
                redisResult = await Database.ScriptEvaluateAsync(_replaceIfEqual, new { key = (RedisKey)key, value = redisValue, expected = expectedValue, expires = expiresIn.Value.TotalSeconds }).AnyContext();
            else
                redisResult = await Database.ScriptEvaluateAsync(_replaceIfEqual, new { key = (RedisKey)key, value = redisValue, expected = expectedValue, expires = "" }).AnyContext();
            
            var result = (int)redisResult;

            return result > 0;
        }

        public async Task<double> IncrementAsync(string key, double amount = 1, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (expiresIn?.Ticks < 0) {
                await this.RemoveAsync(key).AnyContext();
                return -1;
            }

            if (expiresIn.HasValue) {
                await LoadScriptsAsync().AnyContext();
                var result = await Database.ScriptEvaluateAsync(_incrementWithExpire, new { key = (RedisKey)key, value = amount, expires = expiresIn.Value.TotalSeconds }).AnyContext();
                return (double)result;
            }

            return await Database.StringIncrementAsync(key, amount).AnyContext();
        }

        public async Task<long> IncrementAsync(string key, long amount = 1, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (expiresIn?.Ticks < 0) {
                await this.RemoveAsync(key).AnyContext();
                return -1;
            }

            if (expiresIn.HasValue) {
                await LoadScriptsAsync().AnyContext();
                var result = await Database.ScriptEvaluateAsync(_incrementWithExpire, new { key = (RedisKey)key, value = amount, expires = expiresIn.Value.TotalSeconds }).AnyContext();
                return (long)result;
            }

            return await Database.StringIncrementAsync(key, amount).AnyContext();
        }

        public Task<bool> ExistsAsync(string key) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");
            
            return Database.KeyExistsAsync(key);
        }

        public Task<TimeSpan?> GetExpirationAsync(string key) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            return Database.KeyTimeToLiveAsync(key);
        }

        public Task SetExpirationAsync(string key, TimeSpan expiresIn) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (expiresIn.Ticks < 0)
                return this.RemoveAsync(key);

            return Database.KeyExpireAsync(key, expiresIn);
        }

        private IDatabase Database => _options.ConnectionMultiplexer.GetDatabase();

        private async Task LoadScriptsAsync() {
            if (_scriptsLoaded)
                return;

            using (await _lock.LockAsync().AnyContext()) {
                if (_scriptsLoaded)
                    return;

                var removeByPrefix = LuaScript.Prepare(RemoveByPrefixScript);
                var incrementWithExpire = LuaScript.Prepare(IncrementWithScript);
                var removeIfEqual = LuaScript.Prepare(RemoveIfEqualScript);
                var replaceIfEqual = LuaScript.Prepare(ReplaceIfEqualScript);
                var setIfHigher = LuaScript.Prepare(SetIfHigherScript);
                var setIfLower = LuaScript.Prepare(SetIfLowerScript);

                foreach (var endpoint in _options.ConnectionMultiplexer.GetEndPoints()) {
                    var server = _options.ConnectionMultiplexer.GetServer(endpoint);
                    if (server.IsSlave)
                        continue;
                    
                    _removeByPrefix = await removeByPrefix.LoadAsync(server).AnyContext();
                    _incrementWithExpire = await incrementWithExpire.LoadAsync(server).AnyContext();
                    _removeIfEqual = await removeIfEqual.LoadAsync(server).AnyContext();
                    _replaceIfEqual = await replaceIfEqual.LoadAsync(server).AnyContext();
                    _setIfHigher = await setIfHigher.LoadAsync(server).AnyContext();
                    _setIfLower = await setIfLower.LoadAsync(server).AnyContext();
                }

                _scriptsLoaded = true;
            }
        }

        private void ConnectionMultiplexerOnConnectionRestored(object sender, ConnectionFailedEventArgs connectionFailedEventArgs) {
            if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("Redis connection restored.");
            _scriptsLoaded = false;
        }

        public void Dispose() {
            _options.ConnectionMultiplexer.ConnectionRestored -= ConnectionMultiplexerOnConnectionRestored;
        }

        ISerializer IHaveSerializer.Serializer => _options.Serializer;

		private static readonly string RemoveByPrefixScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.RemoveByPrefix.lua");
		private static readonly string IncrementWithScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.IncrementWithExpire.lua");
		private static readonly string RemoveIfEqualScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.RemoveIfEqual.lua");
		private static readonly string ReplaceIfEqualScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.ReplaceIfEqual.lua");
		private static readonly string SetIfHigherScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.SetIfHigher.lua");
		private static readonly string SetIfLowerScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.SetIfLower.lua");
    }
}
