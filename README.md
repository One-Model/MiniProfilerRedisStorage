# MiniProfilerRedisStorage
A Redis storage implementation for MiniProfiler based on HttpRuntimeCacheStorage. Useful when you do not want to persist profiler results into a database, but need to access them across multiple servers and HttpRuntimeCacheStorage won't cut it e.g. behind a load balancer without sticky sessions.

- Squashes exceptions, unlike other storage providers, to prevent errors from exceptions in profiling.
- Results are stored in a hash in Redis. The hash will expire in Redis after the last result added expires.
- Expired results are also removed in the List and Load methods. HttpRuntimeCacheStorage removes expired results in the Save method, but this storage provider removes them in Load to move any performance impact to requests for profiling results.
- User unviewed results are stored in individual sets. A set expires on a duration after it was first added, like the behaviour of  HttpRuntimeCacheStorage.
