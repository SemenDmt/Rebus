﻿using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Rebus.Bus;
using Rebus.Config;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Pipeline;
using Rebus.Threading;
using Rebus.Transport;

namespace Rebus.Workers.ThreadPoolBased
{
    class ThreadPoolWorker : IWorker
    {
        readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        readonly ITransport _transport;
        readonly IPipeline _pipeline;
        readonly IPipelineInvoker _pipelineInvoker;
        readonly ParallelOperationsManager _parallelOperationsManager;
        readonly RebusBus _owningBus;
        readonly Options _options;
        readonly ISyncBackoffStrategy _backoffStrategy;
        readonly Thread _workerThread;
        readonly ILog _log;

        internal ThreadPoolWorker(string name, ITransport transport, IRebusLoggerFactory rebusLoggerFactory, IPipeline pipeline, IPipelineInvoker pipelineInvoker, ParallelOperationsManager parallelOperationsManager, RebusBus owningBus, Options options, ISyncBackoffStrategy backoffStrategy)
        {
            Name = name;
            _log = rebusLoggerFactory.GetCurrentClassLogger();
            _transport = transport;
            _pipeline = pipeline;
            _pipelineInvoker = pipelineInvoker;
            _parallelOperationsManager = parallelOperationsManager;
            _owningBus = owningBus;
            _options = options;
            _backoffStrategy = backoffStrategy;
            _workerThread = new Thread(Run)
            {
                Name = name,
                IsBackground = true
            };
            _workerThread.Start();
        }

        public string Name { get; }

        void Run()
        {
            _log.Debug("Starting (threadpool-based) worker {0}", Name);

            var token = _cancellationTokenSource.Token;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    TryReceiveNextMessage(token);
                }
                catch (TaskCanceledException)
                {
                    // it's fine
                }
                catch (OperationCanceledException)
                {
                    // it's fine
                }
                catch (Exception exception)
                {
                    _log.Error(exception, "Unhandled exception in worker!!");

                    _backoffStrategy.WaitError();
                }
            }

            _log.Debug("Worker {0} stopped", Name);
        }

        void TryReceiveNextMessage(CancellationToken token)
        {
            var parallelOperation = _parallelOperationsManager.TryBegin();

            if (!parallelOperation.CanContinue())
            {
                _backoffStrategy.Wait();
                return;
            }

            Fly(token, parallelOperation);
        }

        async void Fly(CancellationToken token, IDisposable parallelOperation)
        {
            try
            {
                using (parallelOperation)
                using (var context = new DefaultTransactionContext())
                {
                    var transportMessage = await _transport.Receive(context, token);

                    if (transportMessage == null)
                    {
                        context.Dispose();
                        parallelOperation.Dispose();
                        _backoffStrategy.Wait();
                        return;
                    }

                    _backoffStrategy.Reset();

                    try
                    {
                        context.Items["CancellationToken"] = token;
                        context.Items["OwningBus"] = _owningBus;
                        AmbientTransactionContext.Current = context;

                        var incomingSteps = _pipeline.ReceivePipeline();
                        var stepContext = new IncomingStepContext(transportMessage, context);
                        await _pipelineInvoker.Invoke(stepContext, incomingSteps);

                        try
                        {
                            await context.Complete();
                        }
                        catch (Exception exception)
                        {
                            _log.Error(exception, "An error occurred when attempting to complete the transaction context");
                        }
                    }
                    catch (ThreadAbortException exception)
                    {
                        context.Abort();

                        _log.Error(exception, $"Worker was killed while handling message {transportMessage.GetMessageLabel()}");
                    }
                    catch (Exception exception)
                    {
                        context.Abort();

                        _log.Error(exception, $"Unhandled exception while handling message {transportMessage.GetMessageLabel()}");
                    }
                    finally
                    {
                        AmbientTransactionContext.Current = null;
                    }
                }
            }
            catch (AggregateException aggregateException)
            {
                var baseException = aggregateException.GetBaseException();

                if (baseException is TaskCanceledException || baseException is OperationCanceledException)
                {
                    var info = ExceptionDispatchInfo.Capture(baseException);

                    info.Throw();
                }

                throw;
            }
        }

        public void Stop()
        {
            _cancellationTokenSource.Cancel();
        }

        public void Dispose()
        {
            Stop();

            if (!_workerThread.Join(_options.WorkerShutdownTimeout))
            {
                _log.Warn($"The '{Name}' worker did not shut down within {_options.WorkerShutdownTimeout.TotalSeconds} seconds!");

                _workerThread.Abort();
            }
        }
    }
}