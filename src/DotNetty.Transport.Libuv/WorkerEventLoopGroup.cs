﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Transport.Libuv
{
    using System;
    using System.Diagnostics;
    using System.Diagnostics.Contracts;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using DotNetty.Common.Concurrency;
    using DotNetty.Common.Utilities;
    using DotNetty.Transport.Channels;
    using DotNetty.Transport.Libuv.Native;

    public sealed class WorkerEventLoopGroup : IEventLoopGroup
    {
        static readonly int DefaultEventLoopThreadCount = Environment.ProcessorCount;
        static readonly TimeSpan StartTimeout = TimeSpan.FromMilliseconds(500);

        readonly WorkerEventLoop[] eventLoops;
        readonly DispatcherEventLoop dispatcherLoop;
        int requestId;

        public WorkerEventLoopGroup(DispatcherEventLoopGroup eventLoopGroup) 
            : this(eventLoopGroup, DefaultEventLoopThreadCount)
        {
        }

        public WorkerEventLoopGroup(DispatcherEventLoopGroup eventLoopGroup, int eventLoopCount)
        {
            Contract.Requires(eventLoopGroup != null);

            this.dispatcherLoop = eventLoopGroup.Dispatcher;
            this.PipeName = this.dispatcherLoop.PipeName;

            // Wait until the pipe is listening to connect
            this.dispatcherLoop.WaitForLoopRun(StartTimeout);

            this.eventLoops = new WorkerEventLoop[eventLoopCount];
            var terminationTasks = new Task[eventLoopCount];
            for (int i = 0; i < eventLoopCount; i++)
            {
                WorkerEventLoop eventLoop;
                bool success = false;
                try
                {
                    eventLoop = new WorkerEventLoop(this);
                    success = eventLoop.ConnectTask.Wait(StartTimeout);
                    if (!success)
                    {
                        throw new TimeoutException($"Connect to dispatcher pipe {this.PipeName} timed out.");
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to create a child {nameof(WorkerEventLoop)}.", ex.Unwrap());
                }
                finally
                {
                    if (!success)
                    {
                        Task.WhenAll(this.eventLoops.Take(i).Select(loop => loop.ShutdownGracefullyAsync())).Wait();
                    }
                }

                this.eventLoops[i] = eventLoop;
                terminationTasks[i] = eventLoop.TerminationCompletion;
            }

            this.TerminationCompletion = Task.WhenAll(terminationTasks);
        }

        internal string PipeName { get; }

        internal void Accept(NativeHandle handle)
        {
            Debug.Assert(this.dispatcherLoop != null);
            this.dispatcherLoop.Accept(handle);
        }

        public Task TerminationCompletion { get; }

        public IEventLoop GetNext()
        {
            int id = Interlocked.Increment(ref this.requestId);
            return this.eventLoops[Math.Abs(id % this.eventLoops.Length)];
        }

        IEventExecutor IEventExecutorGroup.GetNext() => this.GetNext();

        public Task RegisterAsync(IChannel channel)
        {
            if (!(channel is NativeChannel nativeChannel))
            {
                throw new ArgumentException($"{nameof(channel)} must be of {typeof(NativeChannel)}");
            }

            IntPtr loopHandle = nativeChannel.GetLoopHandle();
            foreach (WorkerEventLoop loop in this.eventLoops)
            {
                if (loop.UnsafeLoop.Handle == loopHandle)
                {
                    return loop.RegisterAsync(nativeChannel);
                }
            }

            throw new InvalidOperationException($"Loop {loopHandle} does not exist");
        }

        public Task ShutdownGracefullyAsync()
        {
            foreach (WorkerEventLoop eventLoop in this.eventLoops)
            {
                eventLoop.ShutdownGracefullyAsync();
            }
            return this.TerminationCompletion;
        }

        public Task ShutdownGracefullyAsync(TimeSpan quietPeriod, TimeSpan timeout)
        {
            foreach (WorkerEventLoop eventLoop in this.eventLoops)
            {
                eventLoop.ShutdownGracefullyAsync(quietPeriod, timeout);
            }
            return this.TerminationCompletion;
        }
    }
}
