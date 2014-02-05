﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.md in the project root for license information.

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR.Configuration;
using Microsoft.AspNet.SignalR.Hosting;
using Microsoft.AspNet.SignalR.Infrastructure;
using Microsoft.AspNet.SignalR.Json;
using Microsoft.AspNet.SignalR.Tracing;
using Newtonsoft.Json;

namespace Microsoft.AspNet.SignalR.Transports
{
    public class LongPollingTransport : ForeverTransport, ITransport
    {
        private readonly IConfigurationManager _configurationManager;

        // This should be ok to do since long polling request never hang around too long
        // so we won't bloat memory
        private const int MaxMessages = 5000;

        public LongPollingTransport(HostContext context, IDependencyResolver resolver)
            : this(context,
                   resolver.Resolve<JsonSerializer>(),
                   resolver.Resolve<ITransportHeartbeat>(),
                   resolver.Resolve<IPerformanceCounterManager>(),
                   resolver.Resolve<ITraceManager>(),
                   resolver.Resolve<IConfigurationManager>())
        {

        }

        public LongPollingTransport(HostContext context,
                                    JsonSerializer jsonSerializer,
                                    ITransportHeartbeat heartbeat,
                                    IPerformanceCounterManager performanceCounterManager,
                                    ITraceManager traceManager,
                                    IConfigurationManager configurationManager)
            : base(context, jsonSerializer, heartbeat, performanceCounterManager, traceManager)
        {
            _configurationManager = configurationManager;
        }

        public override TimeSpan DisconnectThreshold
        {
            get { return _configurationManager.LongPollDelay; }
        }

        private bool IsJsonp
        {
            get
            {
                return !String.IsNullOrEmpty(JsonpCallback);
            }
        }

        private string JsonpCallback
        {
            get
            {
                return Context.Request.QueryString["callback"];
            }
        }

        public override bool SupportsKeepAlive
        {
            get
            {
                return !IsJsonp;
            }
        }

        public override bool RequiresTimeout
        {
            get
            {
                return true;
            }
        }

        protected override bool IsPollRequest
        {
            get
            {
                return Context.Request.LocalPath.EndsWith("/poll", StringComparison.OrdinalIgnoreCase);
            }
        }

        protected override Func<PersistentResponse, object, Task<bool>> MessageHandler
        {
            get
            {
                return OnMessageReceived;
            }
        }

        public override Task KeepAlive()
        {
            if (InitializeTcs == null || !InitializeTcs.Task.IsCompleted)
            {
                return TaskAsyncHelper.Empty;
            }

            // Ensure delegate continues to use the C# Compiler static delegate caching optimization.
            return EnqueueOperation(state => PerformKeepAlive(state), this);
        }

        public override Task Send(PersistentResponse response)
        {
            Heartbeat.MarkConnection(this);

            AddTransportData(response);

            // This overload is only used in response to /connect, /poll and /reconnect requests,
            // so the response will have already been initialized by ProcessMessages.
            var context = new LongPollingTransportContext(this, response);
            return EnqueueOperation(state => PerformPartialSend(state), context);
        }

        public override Task Send(object value)
        {
            var context = new LongPollingTransportContext(this, value);

            // This overload is only used in response to /send requests,
            // so the response will be uninitialized.
            return EnqueueOperation(state => PerformCompleteSend(state), context);
        }

        protected internal override Task InitializeResponse(ITransportConnection connection)
        {
            // Ensure delegate continues to use the C# Compiler static delegate caching optimization.
            return base.InitializeResponse(connection)
                       .Then(s => WriteInit(s), this);
        }

        private static Task WriteInit(LongPollingTransport transport)
        {
            transport.Context.Response.ContentType = transport.IsJsonp ? JsonUtility.JavaScriptMimeType : JsonUtility.JsonMimeType;
            return transport.Context.Response.Flush();
        }

        private static Task<bool> OnMessageReceived(PersistentResponse response, object state)
        {
            var context = (MessageContext)state;
            var transport = (LongPollingTransport)context.Transport;

            response.Reconnect = transport.HostShutdownToken.IsCancellationRequested;

            Task task = TaskAsyncHelper.Empty;

            if (response.Aborted)
            {
                // If this was a clean disconnect then raise the event
                task = context.Transport.Abort();
            }

            if (response.Terminal)
            {
                // If the response wasn't sent, send it before ending the request
                if (!context.ResponseSent)
                {
                    // Ensure delegate continues to use the C# Compiler static delegate caching optimization.
                    return task.Then((ctx, resp) => ctx.Transport.Send(resp), context, response)
                               .Then(() =>
                               {
                                   context.Lifetime.Complete();

                                   return TaskAsyncHelper.False;
                               });
                }

                // Ensure delegate continues to use the C# Compiler static delegate caching optimization.
                return task.Then(() =>
                {
                    context.Lifetime.Complete();

                    return TaskAsyncHelper.False;
                });
            }

            // Mark the response as sent
            context.ResponseSent = true;

            // Send the response and return false
            // Ensure delegate continues to use the C# Compiler static delegate caching optimization.
            return task.Then((ctx, resp) => ctx.Transport.Send(resp), context, response)
                       .Then(() => TaskAsyncHelper.False);
        }

        private static Task PerformKeepAlive(object state)
        {
            var transport = (LongPollingTransport)state;

            if (!transport.IsAlive)
            {
                return TaskAsyncHelper.Empty;
            }

            transport.OutputWriter.Write(' ');
            transport.OutputWriter.Flush();

            return transport.Context.Response.Flush();
        }

        private static Task PerformPartialSend(object state)
        {
            var context = (LongPollingTransportContext)state;

            if (!context.Transport.IsAlive)
            {
                return TaskAsyncHelper.Empty;
            }

            if (context.Transport.IsJsonp)
            {
                context.Transport.OutputWriter.Write(context.Transport.JsonpCallback);
                context.Transport.OutputWriter.Write("(");
            }

            context.Transport.JsonSerializer.Serialize(context.State, context.Transport.OutputWriter);

            if (context.Transport.IsJsonp)
            {
                context.Transport.OutputWriter.Write(");");
            }

            context.Transport.OutputWriter.Flush();

            return context.Transport.Context.Response.Flush();
        }

        private static Task PerformCompleteSend(object state)
        {
            var context = (LongPollingTransportContext)state;

            if (!context.Transport.IsAlive)
            {
                return TaskAsyncHelper.Empty;
            }

            context.Transport.Context.Response.ContentType = JsonUtility.JsonMimeType;

            return PerformPartialSend(state);
        }
        
        private void AddTransportData(PersistentResponse response)
        {
            if (_configurationManager.LongPollDelay != TimeSpan.Zero)
            {
                response.LongPollDelay = (long)_configurationManager.LongPollDelay.TotalMilliseconds;
            }
        }

        private class LongPollingTransportContext
        {
            public object State;
            public LongPollingTransport Transport;

            public LongPollingTransportContext(LongPollingTransport transport, object state)
            {
                State = state;
                Transport = transport;
            }
        }
    }
}
