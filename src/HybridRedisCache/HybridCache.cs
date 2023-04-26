﻿using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Text.Json;

namespace HybridRedisCache;

/// <summary>
/// The HybridCache class provides a hybrid caching solution that stores cached items in both
/// an in-memory cache and a Redis cache. 
/// </summary>
public class HybridCache : IHybridCache, IDisposable
{
    private readonly IDatabase _redisDb;
    private readonly string _instanceId;
    private readonly HybridCachingOptions _options;
    private readonly ISubscriber _redisSubscriber;
    private readonly ILogger _logger;

    private IMemoryCache _memoryCache;
    private string InvalidationChannel => _options.InstanceName + ":invalidate";
    private int retryPublishCounter = 0;
    private int retryDelayMiliseconds = 100;

    /// <summary>
    /// This method initializes the HybridCache instance and subscribes to Redis key-space events 
    /// to invalidate cache entries on all instances. 
    /// </summary>
    /// <param name="redisConnectionString">Redis connection string</param>
    /// <param name="instanceName">Application unique name for redis indexes</param>
    /// <param name="defaultExpiryTime">default caching expiry time</param>
    public HybridCache(HybridCachingOptions option, ILoggerFactory loggerFactory = null)
    {
        option.NotNull(nameof(option));

        _instanceId = Guid.NewGuid().ToString("N");
        _options = option;
        CreateLocalCache();
        var redis = ConnectionMultiplexer.Connect(option.RedisCacheConnectString);
        _redisDb = redis.GetDatabase();
        _redisSubscriber = redis.GetSubscriber();
        _logger = loggerFactory?.CreateLogger(nameof(HybridCache));

        if (string.IsNullOrWhiteSpace(option.InstanceName))
        {
            _options.InstanceName = _instanceId;
        }

        // Subscribe to Redis key-space events to invalidate cache entries on all instances
        _redisSubscriber.Subscribe(InvalidationChannel, OnMessage, CommandFlags.FireAndForget);
        redis.ConnectionRestored += OnReconnect;
    }

    private void CreateLocalCache()
    {
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
    }

    private void OnMessage(RedisChannel channel, RedisValue value)
    {
        // With this implementation, when a key is updated or removed in Redis,
        // all instances of HybridCache that are subscribed to the pub/sub channel will receive a message
        // and invalidate the corresponding key in their local MemoryCache.

        var message = Deserialize<CacheInvalidationMessage>(value.ToString());
        if (message.InstanceId != _instanceId) // filter out messages from the current instance
        {
            foreach (var key in message.CacheKeys)
            {
                _memoryCache.Remove(key);
                LogMessage($"remove local cache that cache key is {key}");
            }
        }
    }

    /// <summary>
    /// On reconnect (flushes local memory as it could be stale).
    /// </summary>
    private void OnReconnect(object sender, ConnectionFailedEventArgs e)
    {
        if (_options.FlushLocalCacheOnBusReconnection)
        {
            LogMessage("Flushing local cache due to bus reconnection");
            _memoryCache.Dispose();
            CreateLocalCache();
        }
    }

    /// <summary>
    /// Exists the specified Key in cache
    /// </summary>
    /// <returns>The exists</returns>
    /// <param name="cacheKey">Cache key</param>
    public bool Exists(string key)
    {
        key.NotNullOrWhiteSpace(nameof(key));
        var cacheKey = GetCacheKey(key);

        // Circuit Breaker may be more better
        try
        {
            if (_redisDb.KeyExists(cacheKey))
                return true;
        }
        catch (Exception ex)
        {
            LogMessage($"Check cache key exists error [{cacheKey}] ", ex);
            if (_options.ThrowIfDistributedCacheError)
            {
                throw;
            }
        }

        return _memoryCache.TryGetValue(cacheKey, out var _);
    }

    /// <summary>
    /// Exists the specified cacheKey async.
    /// </summary>
    /// <returns>The async.</returns>
    /// <param name="cacheKey">Cache key.</param>
    /// <param name="cancellationToken">CancellationToken</param>
    public async Task<bool> ExistsAsync(string key)
    {
        key.NotNullOrWhiteSpace(nameof(key));
        var cacheKey = GetCacheKey(key);

        // Circuit Breaker may be more better
        try
        {
            if (await _redisDb.KeyExistsAsync(cacheKey))
                return true;
        }
        catch (Exception ex)
        {
            LogMessage($"Check cache key [{cacheKey}] exists error", ex);
            if (_options.ThrowIfDistributedCacheError)
            {
                throw;
            }
        }

        return _memoryCache.TryGetValue(cacheKey, out var _);
    }

    /// <summary>
    /// Sets a value in the cache with the specified key.
    /// </summary>
    /// <typeparam name="T">The type of the value to cache.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="expiration">The expiration time for the cache entry. If not specified, the default expiration time is used.</param>
    /// <param name="fireAndForget">Whether to cache the value in Redis without waiting for the operation to complete.</param>
    public void Set<T>(string key, T value, TimeSpan? expiration = null, bool fireAndForget = true)
    {
        key.NotNullOrWhiteSpace(nameof(key));
        var expiry = expiration ?? _options.DefaultExpirationTime;
        var cacheKey = GetCacheKey(key);
        _memoryCache.Set(cacheKey, value, expiry);

        try
        {
            _redisDb.StringSet(cacheKey, Serialize(value), expiry,
                    flags: fireAndForget ? CommandFlags.FireAndForget : CommandFlags.None);
        }
        catch (Exception ex)
        {
            LogMessage($"set cache key [{cacheKey}] error", ex);

            if (_options.ThrowIfDistributedCacheError)
            {
                throw;
            }
        }

        // When create/update cache, send message to bus so that other clients can remove it.
        PublishBus(cacheKey);
    }

    /// <summary>
    /// Asynchronously sets a value in the cache with the specified key.
    /// </summary>
    /// <typeparam name="T">The type of the value to cache.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="expiration">The expiration time for the cache entry. If not specified, the default expiration time is used.</param>
    /// <param name="fireAndForget">Whether to cache the value in Redis without waiting for the operation to complete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, bool fireAndForget = true)
    {
        key.NotNullOrWhiteSpace(nameof(key));
        var expiry = expiration ?? _options.DefaultExpirationTime;
        var cacheKey = GetCacheKey(key);
        _memoryCache.Set(cacheKey, value, expiry);

        try
        {
            await _redisDb.StringSetAsync(cacheKey, Serialize(value), expiry,
                    flags: fireAndForget ? CommandFlags.FireAndForget : CommandFlags.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogMessage($"set cache key [{cacheKey}] error", ex);

            if (_options.ThrowIfDistributedCacheError)
            {
                throw;
            }
        }

        // When create/update cache, send message to bus so that other clients can remove it.
        await PublishBusAsync(cacheKey).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets all.
    /// </summary>
    /// <returns>The all async.</returns>
    /// <param name="value">Value.</param>
    /// <param name="expiration">Expiration.</param>
    /// <typeparam name="T">The 1st type parameter.</typeparam>
    public void SetAll<T>(IDictionary<string, T> value, TimeSpan? expiration = null, bool fireAndForget = true)
    {
        value.NotNullAndCountGTZero(nameof(value));
        var expiry = expiration ?? _options.DefaultExpirationTime;

        foreach (var kvp in value)
        {
            var cacheKey = GetCacheKey(kvp.Key);
            _memoryCache.Set(cacheKey, kvp.Value, expiry);

            try
            {
                _redisDb.StringSet(cacheKey, Serialize(kvp.Value), expiry,
                     flags: fireAndForget ? CommandFlags.FireAndForget : CommandFlags.None);
            }
            catch (Exception ex)
            {
                LogMessage($"set cache key [{cacheKey}] error", ex);

                if (_options.ThrowIfDistributedCacheError)
                {
                    throw;
                }
            }
        }

        // send message to bus 
        PublishBus(value.Keys.ToArray());
    }

    /// <summary>
    /// Sets all async.
    /// </summary>
    /// <returns>The all async.</returns>
    /// <param name="value">Value.</param>
    /// <param name="expiration">Expiration.</param>
    /// <typeparam name="T">The 1st type parameter.</typeparam>
    public async Task SetAllAsync<T>(IDictionary<string, T> value, TimeSpan? expiration = null, bool fireAndForget = true)
    {
        value.NotNullAndCountGTZero(nameof(value));
        var expiry = expiration ?? _options.DefaultExpirationTime;

        foreach (var kvp in value)
        {
            var cacheKey = GetCacheKey(kvp.Key);
            _memoryCache.Set(cacheKey, kvp.Value, expiry);

            try
            {
                await _redisDb.StringSetAsync(cacheKey, Serialize(kvp.Value), expiry,
                     flags: fireAndForget ? CommandFlags.FireAndForget : CommandFlags.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogMessage($"set cache key [{cacheKey}] error", ex);

                if (_options.ThrowIfDistributedCacheError)
                {
                    throw;
                }
            }
        }

        // send message to bus 
        await PublishBusAsync(value.Keys.ToArray());
    }

    /// <summary>
    /// Gets a cached value with the specified key.
    /// </summary>
    /// <typeparam name="T">The type of the cached value.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <returns>The cached value, or null if the key is not found in the cache.</returns>
    public T Get<T>(string key)
    {
        key.NotNullOrWhiteSpace(nameof(key));
        var cacheKey = GetCacheKey(key);
        var value = _memoryCache.Get<T>(cacheKey);
        if (value != null)
        {
            return value;
        }

        LogMessage($"local cache can not get the value of {cacheKey}");

        try
        {
            var redisValue = _redisDb.StringGet(cacheKey);
            if (redisValue.HasValue)
            {
                value = Deserialize<T>(redisValue);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Redis cache get error, [{cacheKey}]", ex);
            if (_options.ThrowIfDistributedCacheError)
                throw;
        }

        if (value != null)
        {
            var expiry = GetExpiration(cacheKey);
            _memoryCache.Set(cacheKey, value, expiry);
            return value;
        }

        LogMessage($"distributed cache can not get the value of {cacheKey}");
        return value;
    }

    /// <summary>
    /// Asynchronously gets a cached value with the specified key.
    /// </summary>
    /// <typeparam name="T">The type of the cached value.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the cached value, or null if the key is not found in the cache.</returns>
    public async Task<T> GetAsync<T>(string key)
    {
        key.NotNullOrWhiteSpace(nameof(key));
        var cacheKey = GetCacheKey(key);
        var value = _memoryCache.Get<T>(cacheKey);
        if (value != null)
        {
            return value;
        }

        LogMessage($"local cache can not get the value of {cacheKey}");

        try
        {
            var redisValue = await _redisDb.StringGetAsync(cacheKey).ConfigureAwait(false);
            if (redisValue.HasValue)
            {
                value = Deserialize<T>(redisValue);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Redis cache get error, [{cacheKey}]", ex);
            if (_options.ThrowIfDistributedCacheError)
                throw;
        }

        if (value != null)
        {
            var expiry = await GetExpirationAsync(cacheKey);
            _memoryCache.Set(cacheKey, value, expiry);
            return value;
        }

        LogMessage($"distributed cache can not get the value of {cacheKey}");
        return value;
    }

    /// <summary>
    /// Removes a cached value with the specified key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    public void Remove(params string[] keys)
    {
        keys.NotNullAndCountGTZero(nameof(keys));
        var cacheKeys = Array.ConvertAll(keys, GetCacheKey);
        try
        {
            // distributed cache at first
            _redisDb.KeyDelete(Array.ConvertAll(cacheKeys, x => (RedisKey)x));
        }
        catch (Exception ex)
        {
            LogMessage($"remove cache key [{string.Join(" | ", keys)}] error", ex);

            if (_options.ThrowIfDistributedCacheError)
            {
                throw;
            }
        }

        Array.ForEach(cacheKeys, _memoryCache.Remove);

        // send message to bus 
        PublishBus(cacheKeys);
    }

    /// <summary>
    /// Asynchronously removes a cached value with the specified key.
    /// </summary>
    /// <param name="keys">The cache key.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task RemoveAsync(params string[] keys)
    {
        keys.NotNullAndCountGTZero(nameof(keys));
        var cacheKeys = Array.ConvertAll(keys, GetCacheKey);
        try
        {
            // distributed cache at first
            await _redisDb.KeyDeleteAsync(Array.ConvertAll(cacheKeys, x => (RedisKey)x)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogMessage($"remove cache key [{string.Join(" | ", keys)}] error", ex);

            if (_options.ThrowIfDistributedCacheError)
            {
                throw;
            }
        }

        Array.ForEach(cacheKeys, _memoryCache.Remove);

        // send message to bus 
        await PublishBusAsync(cacheKeys).ConfigureAwait(false);
    }

    private string GetCacheKey(string key) => $"{_options.InstanceName}:{key}";

    private async Task PublishBusAsync(params string[] cacheKeys)
    {
        cacheKeys.NotNullAndCountGTZero(nameof(cacheKeys));

        try
        {
            // include the instance ID in the pub/sub message payload to update another instances
            var message = new CacheInvalidationMessage(_instanceId, cacheKeys);
            await _redisDb.PublishAsync(InvalidationChannel, Serialize(message)).ConfigureAwait(false);
        }
        catch
        {
            // Retry to publish message
            if (retryPublishCounter++ < _options.BusRetryCount)
            {
                await Task.Delay(retryDelayMiliseconds * retryPublishCounter).ConfigureAwait(false);
                await PublishBusAsync(cacheKeys).ConfigureAwait(false);
            }
        }
    }
    private void PublishBus(params string[] cacheKeys)
    {
        cacheKeys.NotNullAndCountGTZero(nameof(cacheKeys));

        try
        {
            // include the instance ID in the pub/sub message payload to update another instances
            var message = new CacheInvalidationMessage(_instanceId, cacheKeys);
            _redisDb.Publish(InvalidationChannel, Serialize(message));
        }
        catch
        {
            // Retry to publish message
            if (retryPublishCounter++ < _options.BusRetryCount)
            {
                Thread.Sleep(retryDelayMiliseconds * retryPublishCounter);
                PublishBus(cacheKeys);
            }
        }
    }

    private TimeSpan GetExpiration(string cacheKey)
    {
        cacheKey.NotNullOrWhiteSpace(nameof(cacheKey));

        try
        {
            var time = _redisDb.KeyExpireTime(cacheKey);
            return ToTimeSpan(time);
        }
        catch
        {
            return _options.DefaultExpirationTime;
        }
    }

    private async Task<TimeSpan> GetExpirationAsync(string cacheKey)
    {
        cacheKey.NotNullOrWhiteSpace(nameof(cacheKey));

        try
        {
            var time = await _redisDb.KeyExpireTimeAsync(cacheKey);
            return ToTimeSpan(time);
        }
        catch
        {
            return _options.DefaultExpirationTime;
        }
    }

    /// <summary>
    /// Logs the message.
    /// </summary>
    /// <param name="message">Message.</param>
    /// <param name="ex">Ex.</param>
    private void LogMessage(string message, Exception ex = null)
    {
        if (_options.EnableLogging && _logger is not null)
        {
            if (ex is null)
            {
                _logger.LogDebug(message);
            }
            else
            {
                _logger.LogError(ex, message);
            }
        }
    }

    private TimeSpan ToTimeSpan(DateTime? time)
    {
        TimeSpan duration = TimeSpan.Zero;

        if (time.HasValue)
        {
            duration = time.Value.Subtract(DateTime.Now);
        }

        if (duration <= TimeSpan.Zero)
        {
            duration = _options.DefaultExpirationTime;
        }

        return duration;
    }

    private static string Serialize<T>(T value)
    {
        if (value == null)
        {
            return null;
        }

        return JsonSerializer.Serialize(value);
    }

    private static T Deserialize<T>(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(value);
    }

    public void Dispose()
    {
        _redisSubscriber?.UnsubscribeAll();
        _redisDb?.Multiplexer?.Dispose();
        _memoryCache?.Dispose();
    }
}