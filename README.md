# ResilientCommand
A Resiliency library for wrapping dependency calls. Heavily inspired by Hystrix


The flow is simple
1. We wrap execution in a timeout (... timeout)
2. We then wrap that in a circuitbreaker (Failure Threshhold, over duration of time.)
3. and further wrap that in a semaphore (maxParalellism)

A cache can be enabled by overriding `GetCacheKey()` which will cause subsequent calls to fetch the result from that cache.

A fallback can be enabled by overriding `Fallback()` which will cause any exceptions that are thrown to be handled and return the fallback value.
The fallback value is ment to be a static or at least local value.

Some future developments
* Inject ILogger
* Metrics for failures.
