using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Marille;

/// <summary>
/// Main implementation of the IHub interface. This class is responsible for managing the topics and the workers.
/// </summary>
public class Hub : IHub {
	readonly SemaphoreSlim semaphoreSlim;
	readonly Dictionary<string, Topic> topics = new();
	public Channel<WorkerError> WorkersExceptions { get; } = Channel.CreateUnbounded<WorkerError> ();

	public Hub () : this (new (1))  { }
	internal Hub (SemaphoreSlim semaphore)
	{
		semaphoreSlim = semaphore;
	} 
	
	void DeliverAtLeastOnce<T> (Channel<Message<T>> channel, IWorker<T> [] workersArray, Message<T> item, TimeSpan? timeout)
		where T : struct
	{
		Parallel.ForEach (workersArray, worker => {
			CancellationToken token = default;
			if (timeout.HasValue) {
				var cts = new CancellationTokenSource ();
				cts.CancelAfter (timeout.Value);
				token = cts.Token;
			}
			_ = worker.ConsumeAsync (item.Payload, token)
				.ContinueWith ((t) => { channel.Writer.WriteAsync (item); }, TaskContinuationOptions.OnlyOnCanceled) // TODO: max retries
				.ContinueWith ((t) => WorkersExceptions.Writer.WriteAsync (new(typeof(T), worker, t.Exception)), 
					TaskContinuationOptions.OnlyOnFaulted);
		});
	}

	Task<ValueTask> DeliverAtMostOnce<T> (Channel<Message<T>> ch, IWorker<T> [] workersArray, Message<T> item, TimeSpan? timeout)
		where T : struct
	{
		// we do know we are not empty, and in the AtMostOnce mode we will only use the first worker
		// present
		var worker = workersArray [0];
		CancellationToken token = default;
		if (timeout.HasValue) {
			var cts = new CancellationTokenSource ();
			cts.CancelAfter (timeout.Value);
			token = cts.Token;
		}

		var task = worker.ConsumeAsync (item.Payload, token)
			.ContinueWith ((t) => { ch.Writer.WriteAsync (item); },
				TaskContinuationOptions.OnlyOnCanceled) // TODO: max retries
			.ContinueWith (
				(t) => WorkersExceptions.Writer.WriteAsync (new(typeof (T), worker,
					t.Exception)),
				TaskContinuationOptions.OnlyOnFaulted);
		return task;
	}

	async Task ConsumeChannel<T> (TopicConfiguration configuration, Channel<Message<T>> ch, IWorker<T>[] workersArray, 
		TaskCompletionSource<bool> completionSource, CancellationToken cancellationToken) where T : struct
	{
		// this is an important check, else the items will be consumer with no worker to receive them
		if (workersArray.Length == 0) {
			completionSource.SetResult (true);
			return;
		}

		// we want to set the completion source to true ONLY when we are consuming, that happens the first time
		// we have a WaitToReadAsync result. The or will ensure we do not call try set result more than once
		while (await ch.Reader.WaitToReadAsync (cancellationToken) 
		       && (completionSource.Task.IsCompleted || completionSource.TrySetResult (true))) {
			while (ch.Reader.TryRead (out var item)) {
				// filter the ack message since it is only used to make sure that the task is indeed consuming
				if (item.Type == MessageType.Ack) 
					continue;
				switch (configuration.Mode) {
				case ChannelDeliveryMode.AtLeastOnce:
					DeliverAtLeastOnce (ch, workersArray, item, configuration.Timeout);
					break;
				case ChannelDeliveryMode.AtMostOnceAsync:
					_ = DeliverAtMostOnce (ch, workersArray, item, configuration.Timeout);
					break;
				case ChannelDeliveryMode.AtMostOnceSync:
					// make the call 'sync' by not processing an item until we are done with the current one
					await DeliverAtMostOnce (ch, workersArray, item, configuration.Timeout);
					break;
				}
			}
		}
	}

	async Task<bool> StartConsuming<T> (TopicInfo<T> topicInfo)
		where T : struct
	{
		// we want to be able to cancel the thread that we are using to consume the
		// events for two different reasons:
		// 1. We are done with the work
		// 2. We want to add a new worker. Rather than risk a weird state
		//    in which we are running a thread and try to modify a collection, 
		//    we cancel the thread, use the channel as a buffer and do the changes
		if (topicInfo.CancellationTokenSource is not null)
			await topicInfo.CancellationTokenSource.CancelAsync ();

		// create a new source for the topic, we cannot use the one that we used to cancel the previous one
		topicInfo.CancellationTokenSource = new ();
		var workersCopy = topicInfo.Workers.ToArray (); 

		// we have no interest in awaiting for this task, but we want to make sure it started. To do so
		// we create a TaskCompletionSource that will be set when the consume channel method is ready to consume
		var completionSource = new TaskCompletionSource<bool>();
		topicInfo.ConsumerTask = ConsumeChannel (
			topicInfo.Configuration, topicInfo.Channel, workersCopy, completionSource, topicInfo.CancellationTokenSource.Token);
		// send a message with a ack so that we can ensure we are indeed running
		_ = topicInfo.Channel.Writer.WriteAsync (new (MessageType.Ack), topicInfo.CancellationTokenSource.Token);
		return await completionSource.Task;
	}

	void StopConsuming<T> (string topicName) where T : struct
	{
		if (!topics.TryGetValue (topicName, out var topic))
			return;
		if (!topic.TryGetChannel<T> (out var topicInfo))
			return;
		topicInfo.CancellationTokenSource?.Cancel ();
	}

	async Task StopConsumingAsync <T> (string topicName) where T : struct
	{
		if (!TryGetChannel<T> (topicName, out var topic, out var topicInfo))
			return;

		// complete the channels, this wont throw an cancellation exception, it will stop the channels from writing
		// and the consuming task will finish when it is done with the current message, therefore we can
		// use that to know when we are done
		topic.CloseChannel<T> ();
		if (topicInfo.ConsumerTask is not null)
			await topicInfo.ConsumerTask;

		// clean behind us
		topic.RemoveChannel<T> ();
	}

	bool TryGetChannel<T> (string topicName, [NotNullWhen(true)] out Topic? topic, [NotNullWhen(true)] out TopicInfo<T>? ch) where T : struct
	{
		topic = null;
		ch = null;
		if (!topics.TryGetValue (topicName, out topic)) {
			return false;
		}

		if (!topic.TryGetChannel (out ch)) {
			return false;
		}

		return true;
	}

	public async Task<bool> CreateAsync<T> (string topicName, TopicConfiguration configuration,
		params IWorker<T>[] initialWorkers) where T : struct
	{
		if (configuration.Mode == ChannelDeliveryMode.AtMostOnceAsync && initialWorkers.Length > 1)
			return false;

		// the topic might already have the channel, in that case, do nothing
		await semaphoreSlim.WaitAsync ();
		try {
			if (!topics.TryGetValue (topicName, out Topic? topic)) {
				topic = new(topicName);
				topics [topicName] = topic;
			}

			if (topic.TryGetChannel<T> (out _)) {
				return false;
			}

			var topicInfo = topic.CreateChannel (configuration, initialWorkers);
			await StartConsuming (topicInfo);
			return true;
		} finally {
			semaphoreSlim.Release ( );
		}
	}

	public Task<bool> CreateAsync<T> (string topicName, TopicConfiguration configuration,
		IEnumerable<IWorker<T>> initialWorkers) where T : struct
		=> CreateAsync (topicName, configuration, initialWorkers.ToArray ());

	public Task<bool> CreateAsync<T> (string topicName, TopicConfiguration configuration,
		params Func<T, CancellationToken, Task> [] actions) where T : struct
		=> CreateAsync (topicName, configuration, actions.Select (a => new LambdaWorker<T> (a)));

	public Task<bool> CreateAsync<T> (string topicName, TopicConfiguration configuration) where T : struct
		=> CreateAsync (topicName, configuration, Array.Empty<IWorker<T>> ());
	
	public Task<bool> CreateAsync<T> (string topicName, TopicConfiguration configuration,
		Func<T, CancellationToken, Task> action) where T : struct
		=> CreateAsync (topicName, configuration, new LambdaWorker<T> (action));

	public async Task<bool> RegisterAsync<T> (string topicName, params IWorker<T>[] newWorkers) where T : struct
	{
		await semaphoreSlim.WaitAsync ();
		try {
			// we only allow the client to register to an existing topic
			// in this API we will not create it, there are other APIs for that
			if (!TryGetChannel<T> (topicName, out _, out var topicInfo))
				return false;

			// do not allow to add more than one worker if we are in AtMostOnce mode.
			if (topicInfo.Configuration.Mode == ChannelDeliveryMode.AtMostOnceAsync && topicInfo.Workers.Count >= 1)
				return false;

			// we will have to stop consuming while we add the new worker
			// but we do not need to close the channel, the API will buffer
			StopConsuming<T> (topicName);
			topicInfo.Workers.AddRange (newWorkers);
			return await StartConsuming (topicInfo);
		} finally {
			semaphoreSlim.Release ();
		}
	}

	public Task<bool> RegisterAsync<T> (string topicName, Func<T, CancellationToken, Task> action)  where T : struct
		=> RegisterAsync (topicName, new LambdaWorker<T> (action));

	public ValueTask Publish<T> (string topicName, T publishedEvent) where T : struct
	{
		if (!TryGetChannel<T> (topicName, out _, out var topicInfo))
			throw new InvalidOperationException (
				$"Channel with topic {topicName} for event type {typeof(T)} not found");
		var message = new Message<T> (MessageType.Data, publishedEvent);
		return topicInfo.Channel.Writer.WriteAsync (message);
	}

	public async Task CloseAllAsync ()
	{
		// we are using this format to ensure that we have the right nullable types, if we where to use the following
		// 
		// var consumingTasks = cancellationTokenSources.Values
		//	.Select (x => x.ConsumeTask).Where (x => x is not null).ToArray ();
		// 
		// the compiler will force use to later do 
		//
		// `Task.WhenAll (consumingTasks!);` 
		//
		// suppressing the warning is ugly when we do know how to help the compiler ;)
		await semaphoreSlim.WaitAsync ();
		try {
			var consumingTasks = from topic in topics.Values
				let tasks = topic.ConsumerTasks
				from task in tasks select task;

			var cancellationTasks = from topic in topics.Values
				let cancellationTokens = topic.CancellationTokenSources
				from source in cancellationTokens select source.CancelAsync ();

			// we could do a nested Task.WhenAll but we want to ensure that the cancellation tasks are done before
			await Task.WhenAll (cancellationTasks);
			await Task.WhenAll (consumingTasks);
		} finally {
			semaphoreSlim.Release ();
		}
	}

	public async Task<bool> CloseAsync<T> (string topicName) where T : struct
	{
		await semaphoreSlim.WaitAsync ();
		try {
			// ensure that the channels does exist, if not, return false
			if (!TryGetChannel<T> (topicName, out _, out _))
				return false;
			await StopConsumingAsync<T> (topicName);
			return true;
		} finally {
			semaphoreSlim.Release ();
		}
	}
}
