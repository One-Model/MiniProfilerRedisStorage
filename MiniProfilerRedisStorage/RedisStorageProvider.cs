using System;
using System.Collections.Generic;
using System.Linq;
using StackExchange.Profiling;
using StackExchange.Profiling.Storage;
using StackExchange.Redis;

namespace MiniProfilerRedisStorage
{
    /// <summary>
    /// Redis storage for MiniProfiler results.
    /// </summary>
    public class RedisStorageProvider : IStorage
    {
        /// <summary>
        /// The key that the hash with all MiniProfilers are stored in.
        /// </summary>
        private const string ResultsKey = "mini-profiler-results";

        /// <summary>
        /// Prefix for the key that unviewed results for a user are stored in, e.g.
        /// <c>"mini-profiler-unviewed-for-user-::1".</c>
        /// </summary>
        private const string UnviewedUserPrefix = "mini-profiler-unviewed-for-user-";

        private readonly IConnectionMultiplexer _connectionMultiplexer;

        public RedisStorageProvider(IConnectionMultiplexer connectionMultiplexer, TimeSpan cacheDuration)
        {
            _connectionMultiplexer = connectionMultiplexer;
            CacheDuration = cacheDuration;
        }

        /// <summary>
        /// Gets or sets how long to cache each <see cref="MiniProfiler"/> for, in absolute terms.
        /// </summary>
        public TimeSpan CacheDuration { get; set; }

        /// <summary>
        /// Returns a list of <see cref="MiniProfiler.Id"/>s that haven't been seen by <paramref name="user"/>.
        /// </summary>
        /// <param name="user">
        /// User identified by the current <c>MiniProfiler.Settings.UserProvider</c>.
        /// </param>
        public List<Guid> GetUnviewedIds(string user)
        {
            try
            {
                var database = _connectionMultiplexer.GetDatabase();
                var key = GetPerUserUnviewedCacheKey(user);
                var result = database.SetMembers(key);
                return result.Select(item => new Guid((string)item)).ToList();
            }
            catch
            {
                return new List<Guid>();
            }
        }

        /// <summary>
        /// List the latest profiling results.
        /// </summary>
        public IEnumerable<Guid> List(
            int maxResults,
            DateTime? start = default(DateTime?),
            DateTime? finish = default(DateTime?),
            ListResultsOrder orderBy = ListResultsOrder.Descending)
        {
            try
            {
                var database = _connectionMultiplexer.GetDatabase();
                var profiles = GetAllProfiles(database);

                RemoveExpiredProfiles(database, profiles);

                var unexpiredProfiles = profiles.Where(profile => !ProfileHasExpired(profile));
                var results = unexpiredProfiles;

                if (start.HasValue)
                {
                    results = results.Where(profile => profile.Started.ToUniversalTime() > start.Value.ToUniversalTime());
                }

                if (finish.HasValue)
                {
                    results = results.Where(profile => profile.Started.ToUniversalTime() < finish.Value.ToUniversalTime());
                }

                if (orderBy == ListResultsOrder.Ascending)
                {
                    results = results.OrderBy(profile => profile.Started);
                }
                else
                {
                    results = results.OrderByDescending(profile => profile.Started);
                }

                return results
                    .Take(maxResults)
                    .Select(profile => profile.Id)
                    .ToList();
            }
            catch
            {
                return new List<Guid>();
            }
        }

        /// <summary>
        /// Returns the saved <see cref="MiniProfiler"/> identified by <paramref name="id"/>.
        /// </summary>
        public MiniProfiler Load(Guid id)
        {
            try
            {
                var database = _connectionMultiplexer.GetDatabase();
                var hashField = id.ToString();
                var value = database.HashGet(ResultsKey, hashField);
                var profiler = MiniProfiler.FromJson(value);

                // Expired results removed here, as opposed to Save() as in HttpRuntimeCacheStorage,
                // so that any delay occurs in requests for mini profiler results not requests for pages.
                RemoveExpiredProfiles(database, GetAllProfiles(database));

                return profiler;
            }
            catch
            {
                // Potential for this null return to cause issues. If Load was called from 
                // MiniProfilerHandler.GetListJson the null return will cause a NullReferenceException.
                // This could occur if there is a failure with Redis in between calling the List method and
                // loading the profiles or if a profile is removed (e.g. it expired in between).
                return null;
            }
        }

        /// <summary>
        /// Saves <paramref name="profiler"/> to the Redis under a key concatenated with <see cref="CacheKeyPrefix"/>
        /// and the parameter's <see cref="MiniProfiler.Id"/>.
        /// </summary>
        public void Save(MiniProfiler profiler)
        {
            try
            {
                var database = _connectionMultiplexer.GetDatabase();
                var hashField = profiler.Id.ToString();
                var hashValue = MiniProfiler.ToJson(profiler);
                database.HashSet(ResultsKey, hashField, hashValue);

                // Sliding expiry to ensure that all results are removed if there is no action after this.
                database.KeyExpire(ResultsKey, CacheDuration);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Remembers we did not view the profile.
        /// </summary>
        public void SetUnviewed(string user, Guid id)
        {
            try
            {
                var database = _connectionMultiplexer.GetDatabase();
                var key = GetPerUserUnviewedCacheKey(user);
                database.SetAdd(key, id.ToString());

                // To be consistent with HttpRuntimeCacheStorage the user unviewed list has an
                // absolute expiry from the time the first entry is added.
                var setExpiry = database.KeyTimeToLive(key);
                if (!setExpiry.HasValue)
                {
                    database.KeyExpire(key, CacheDuration);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Set the profile to viewed for this user
        /// </summary>
        public void SetViewed(string user, Guid id)
        {
            try
            {
                var database = _connectionMultiplexer.GetDatabase();
                var key = GetPerUserUnviewedCacheKey(user);
                database.SetRemove(key, id.ToString());
            }
            catch
            {
            }
        }

        private List<MiniProfiler> GetAllProfiles(IDatabase database)
        {
            var results = database.HashGetAll(ResultsKey);
            return results.Select(result => MiniProfiler.FromJson(result.Value))
                .ToList();
        }

        private string GetPerUserUnviewedCacheKey(string user)
        {
            return UnviewedUserPrefix + user;
        }

        private bool ProfileHasExpired(MiniProfiler profile)
        {
            return profile.Started.ToUniversalTime() < DateTime.UtcNow.Add(-CacheDuration);
        }

        private void RemoveExpiredProfiles(IDatabase database, IEnumerable<MiniProfiler> profiles)
        {
            var expiredProfileIds = profiles.Where(ProfileHasExpired)
                .Select(profile => (RedisValue)profile.Id.ToString())
                .ToArray();
            database.HashDelete(ResultsKey, expiredProfileIds);
        }
    }
}
