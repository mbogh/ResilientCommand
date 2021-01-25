using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace ResilientCommand
{
    public abstract class ResilientCommand<TResult> where TResult : class
    {
        private readonly CircuitBreaker circuitBreaker;
        private readonly CommandKey commandKey;
        private readonly CommandConfiguration configuration;
        private readonly ResilientCommandEventNotifier eventNotifier;
        private readonly ExecutionTimeout executionTimeout;
        private readonly ConcurrentDictionary<string, TResult> resultCache = new ConcurrentDictionary<string, TResult>();
        private readonly SemaphoreSlim semaphore;

        public ResilientCommand(CommandKey commandKey = null, CommandConfiguration configuration = null)
        {
            this.commandKey = commandKey ?? new CommandKey(GetType().Name);
            this.configuration = configuration ?? CommandConfiguration.CreateConfiguration();

            eventNotifier = InitEventNotifier();
            circuitBreaker = InitCircuitBreaker();
            executionTimeout = InitExecutionTimeout();
            semaphore = InitSemaphore();

        }

        private bool IsCachedResponseEnabled => GetCacheKey() != null;

        public async Task<TResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            string cacheKey = $"{commandKey}_{GetCacheKey()}";

            TResult result;
            if (IsCachedResponseEnabled && resultCache.TryGetValue(cacheKey, out result))
            {
                this.eventNotifier.MarkEvent(ResillientCommandEventType.ResponseFromCache, this.commandKey);
                return result;
            }

            try
            {
                await semaphore.WaitAsync();

                result = await WrappedExecutionAsync(cancellationToken);

                if (IsCachedResponseEnabled)
                {
                    resultCache.TryAdd(cacheKey, result);
                }

                return result;
            }
            catch (Exception ex)
            {
                switch (ex)
                {
                    case CircuitBrokenException:
                    case FallbackNotImplementedException:
                        throw;
                    default:
                        break;
                }

                this.eventNotifier.markEvent(ResillientCommandEventType.ExceptionThrown, this.commandKey);

                return HandleFallback(ex);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Override this to enable fallback.
        /// </summary>
        /// <returns></returns>
        protected virtual TResult Fallback()
        {
            return null;
        }

        /// <summary>
        /// Override this to enable caching.
        /// The cache will be per <see cref="CommandKey"/>.
        /// </summary>
        /// <returns></returns>
        protected virtual string GetCacheKey()
        {
            return null;
        }

        /// <summary>
        /// Task to run as a ResilientCommand.
        /// </summary>
        /// <remarks>
        /// In order for the CircuitBreaker to work, please re-throw any exceptions
        /// </remarks>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        protected abstract Task<TResult> RunAsync(CancellationToken cancellationToken);

        private TResult HandleFallback(Exception innerException)
        {
            if (!this.configuration.FallbackEnabled)
            {
                this.eventNotifier.MarkEvent(ResillientCommandEventType.FallbackSkipped, this.commandKey);
                throw innerException;
            }
            var fallbackValue = Fallback();
            if (fallbackValue != null)
            {
                this.eventNotifier.MarkEvent(ResillientCommandEventType.FallbackSuccess, this.commandKey);
                return fallbackValue;
            }

            throw new FallbackNotImplementedException(this.commandKey, innerException);
        }
        private CircuitBreaker InitCircuitBreaker()
        {
            if (this.configuration.CircuitBreakerSettings.IsEnabled)
            {
                return CircuitBreakerFactory.GetInstance().GetOrCreateCircuitBreaker(this.commandKey, this.eventNotifier, this.configuration.CircuitBreakerSettings);
            }

            return null;
        }

        private ResilientCommandEventNotifier InitEventNotifier()
        {
            return EventNotifierFactory.GetInstance().GetEventNotifier();
        }

        private ExecutionTimeout InitExecutionTimeout()
        {
            if (this.configuration.ExecutionTimeoutSettings.IsEnabled)
            {
                return ExecutionTimeoutFactory.GetInstance().GetOrCreateExecutionTimeout(this.commandKey, this.eventNotifier, this.configuration.ExecutionTimeoutSettings);
            }

            return null;
        }

        private SemaphoreSlim InitSemaphore()
        {
            return SemaphoreFactory.GetOrCreateSemaphore(commandKey, this.configuration.MaxParallelism);
        }

        private async Task<TResult> WrappedExecutionAsync(CancellationToken cancellationToken)
        {
            Task<TResult> timeoutTask = null;
            if (this.configuration.ExecutionTimeoutSettings.IsEnabled)
            {
                timeoutTask = executionTimeout.ExecuteAsync(RunAsync, cancellationToken);
            }

            Task<TResult> circuitBreakerTask = null;
            if (this.configuration.CircuitBreakerSettings.IsEnabled)
            {
                circuitBreakerTask = circuitBreaker.ExecuteAsync(
                    innerAction: (ct) => timeoutTask ?? RunAsync(ct), 
                    onBrokenCircuit: () => HandleFallback(new CircuitBrokenException(this.commandKey)), 
                    cancellationToken);
            }

            Task<TResult> resultTask = circuitBreakerTask ?? RunAsync(cancellationToken);

            return await resultTask;
        }
    }
}
