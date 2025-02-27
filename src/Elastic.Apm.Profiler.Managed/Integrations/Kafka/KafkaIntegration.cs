﻿// Licensed to Elasticsearch B.V under
// one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information
//
// <copyright file="KafkaHelper.cs" company="Datadog">
// Unless explicitly stated otherwise all files in this repository are licensed under the Apache 2 License.
// This product includes software developed at Datadog (https://www.datadoghq.com/). Copyright 2017 Datadog, Inc.
// </copyright>

using System;
using System.Linq;
using Elastic.Apm.Api;
using Elastic.Apm.DistributedTracing;
using Elastic.Apm.Helpers;
using Elastic.Apm.Logging;
using Elastic.Apm.Model;

namespace Elastic.Apm.Profiler.Managed.Integrations.Kafka
{
    internal static class KafkaIntegration
    {
		internal const string TopicPartitionTypeName = "Confluent.Kafka.TopicPartition";
		internal const string MessageTypeName = "Confluent.Kafka.Message`2[!0,!1]";
		internal const string ConsumeResultTypeName = "Confluent.Kafka.ConsumeResult`2[!0,!1]";
		internal const string ActionOfDeliveryReportTypeName = "System.Action`1[Confluent.Kafka.DeliveryReport`2[!0,!1]]";
		internal const string TaskDeliveryReportTypeName = "System.Threading.Tasks.Task`1[Confluent.Kafka.DeliveryReport`2[!0,!1]]";
		internal const string Subtype = "kafka";
		internal const string Name = "Kafka";

		private static bool HeadersInjectionEnabled = true;
		private const double MaxAge = long.MaxValue;

		internal static ISpan CreateProducerSpan(IApmAgent agent, ITopicPartition topicPartition, bool isTombstone, bool finishOnClose)
        {
            ISpan span = null;

            try
            {
				// no current transaction, don't create a span
				var currentTransaction = agent.Tracer.CurrentTransaction;
				if (currentTransaction is null)
					return span;

				var topic = topicPartition?.Topic;
				var matcher = WildcardMatcher.AnyMatch(currentTransaction.Configuration.IgnoreMessageQueues, topic);
				if (matcher != null)
				{
					agent.Logger.Trace()
						?.Log(
							"Not tracing message from {Queue} because it matched IgnoreMessageQueues pattern {Matcher}",
							topic,
							matcher.GetMatcher());

					return span;
				}

				var spanName = string.IsNullOrEmpty(topic)
					? "Kafka SEND"
					: $"Kafka SEND to {topic}";

				span = agent.GetCurrentExecutionSegment().StartSpan(
					spanName,
					ApiConstants.TypeMessaging,
					Subtype,
					isExitSpan: true);

				if (!string.IsNullOrEmpty(topic))
					span.Context.Message = new Message { Queue = new Queue { Name = topic } };

                if (topicPartition?.Partition is not null && !topicPartition.Partition.IsSpecial)
					span.SetLabel("partition", topicPartition.Partition.ToString());

				if (isTombstone)
					span.SetLabel("tombstone", "true");
			}
            catch (Exception ex)
            {
                agent.Logger.Error()?.LogException(ex, "Error creating or populating kafka span.");
            }

            return span;
        }

        internal static ITransaction CreateConsumerTransaction(
            IApmAgent agent,
            string topic,
            Partition? partition,
            Offset? offset,
            IMessage message)
        {
            ITransaction transaction = null;

            try
            {
				if (agent.Tracer.CurrentTransaction is not null)
					return null;

				if (agent is ApmAgent apmAgent)
				{
					var matcher = WildcardMatcher.AnyMatch(apmAgent.ConfigurationStore.CurrentSnapshot.IgnoreMessageQueues, topic);
					if (matcher != null)
					{
						agent.Logger.Trace()
							?.Log(
								"Not tracing message from {Queue} because it matched IgnoreMessageQueues pattern {Matcher}",
								topic,
								matcher.GetMatcher());

						return null;
					}
				}

				DistributedTracingData distributedTracingData = null;
				if (message?.Headers != null)
                {
                    var headers = new KafkaHeadersCollection(message.Headers, agent.Logger);
					try
					{
						var traceParent = string.Join(",", headers.GetValues(TraceContext.TraceParentBinaryHeaderName));
						var traceState = headers.GetValues(TraceContext.TraceStateHeaderName).FirstOrDefault();
						distributedTracingData = TraceContext.TryExtractTracingData(traceParent, traceState);
                    }
                    catch (Exception ex)
                    {
                        agent.Logger.Error()?.LogException(ex, "Error extracting propagated headers from Kafka message");
                    }
                }

				var name = string.IsNullOrEmpty(topic)
					? "Kafka RECEIVE"
					: $"Kafka RECEIVE from {topic}";

				transaction = agent.Tracer.StartTransaction(name, ApiConstants.TypeMessaging, distributedTracingData);

                if (partition is not null)
					transaction.SetLabel("partition", partition.ToString());

				if (offset is not null)
					transaction.SetLabel("offset", offset.ToString());

				// record only queue topic name and age on context for now. capture body and headers potentially in future
				transaction.Context.Message = new Message { Queue = new Queue { Name = topic } };
				if (transaction is Transaction realTransaction && message is not null && message.Timestamp.Type != 0)
				{
					var consumeTime = TimeUtils.ToDateTime(realTransaction.Timestamp);
                    var produceTime = message.Timestamp.UtcDateTime;

					var age = Math.Max(0, (consumeTime - produceTime).TotalMilliseconds);
					if (age > 0 && age < MaxAge)
						transaction.Context.Message.Age = new Age { Ms = (long)age };
				}

                if (message is not null && message.Value is null)
					transaction.SetLabel("tombstone", "true");
			}
            catch (Exception ex)
            {
				agent.Logger.Error()?.LogException(ex, "Error creating or populating transaction.");
            }

            return transaction;
        }

        internal static void CloseConsumerTransaction(IApmAgent agent)
        {
            try
            {
				var transaction = agent.Tracer.CurrentTransaction;
				if (transaction is null || !transaction.Name.StartsWith("Kafka RECEIVE"))
					return;

                transaction.End();
            }
            catch (Exception ex)
            {
                agent.Logger.Error()?.LogException(ex, "Error closing Kafka consumer transaction");
            }
        }

		/// <summary>
		/// Try to inject the trace context into the Kafka message headers
		/// </summary>
		/// <param name="agent">The agent</param>
		/// <param name="segment">The outgoing distributed tracing data to propagate</param>
		/// <param name="message">The duck-typed Kafka Message object</param>
		/// <typeparam name="TTopicPartitionMarker">The TopicPartition type (used  optimisation purposes)</typeparam>
		/// <typeparam name="TMessage">The type of the duck-type proxy</typeparam>
		internal static void TryInjectHeaders<TTopicPartitionMarker, TMessage>(IApmAgent agent, IExecutionSegment segment, TMessage message)
            where TMessage : IMessage
        {
            if (!HeadersInjectionEnabled)
				return;

			try
            {
                message.Headers ??= CachedMessageHeadersHelper<TTopicPartitionMarker>.CreateHeaders();
				var adapter = new KafkaHeadersCollection(message.Headers, agent.Logger);
				var distributedTracingData = segment.OutgoingDistributedTracingData;
				adapter.Set(TraceContext.TraceParentBinaryHeaderName, distributedTracingData.SerializeToString());
				adapter.Set(TraceContext.TraceStateHeaderName, distributedTracingData.TraceState.ToTextHeader());
			}
            catch (Exception ex)
            {
                // don't keep trying if we run into problems
                HeadersInjectionEnabled = false;
                agent.Logger.Warning()?.LogException(ex, "There was a problem injecting headers into the Kafka record. Disabling headers injection");
            }
        }
    }
}
