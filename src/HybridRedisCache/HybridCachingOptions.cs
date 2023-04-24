﻿namespace HybridRedisCache
{
    public class HybridCachingOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether loggin is enable or not.
        /// </summary>
        /// <value><c>true</c> if enable logging; otherwise, <c>false</c>.</value>
        public bool EnableLogging { get; set; } = false;

        /// <summary>
        /// Gets or sets a value indicating whether an exception should be thrown if an error on the distributed cache has occurred
        /// </summary>
        /// <value><c>true</c> if distributed cache exceptions should not be ignored; otherwise, <c>false</c>.</value>
        public bool ThrowIfDistributedCacheError { get; set; } = false;

        /// <summary>
        /// Redis cache connection string
        /// </summary>
        public string RedisCacheConnectString { get; set; } = "localhost:6379";

        /// <summary>
        /// Gets or sets the name of the instance.
        /// </summary>
        public string InstanceName { get; set; }

        /// <summary>
        /// Gets or sets a expiry time of caches 
        /// </summary>        
        public TimeSpan DefaultExpirationTime { get; set; } = TimeSpan.FromDays(60);

        /// <summary>
        /// The bus retry count.
        /// </summary>
        /// <remarks>
        /// When sending message failed, we will retry some times, default is 3 times.
        /// </remarks>

        public int BusRetryCount { get; set; } = 3;

        /// <summary>
        /// Flush the local cache on bus disconnection/reconnection
        /// </summary>
        /// <remarks>
        /// Flushing the local cache will avoid using stale data but may cause app jitters until the local cache get's re-populated.
        /// </remarks>
        public bool FlushLocalCacheOnBusReconnection { get; set; } = false;
    }
}
