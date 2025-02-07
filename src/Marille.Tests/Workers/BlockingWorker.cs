using Marille.Tests.Workers;

namespace Marille.Tests;

// this worked will block not consume events until the task completion source is triggered, this way
// we can add a delay to the worker and check that several messages are present in the channel
public class BlockingWorker (TaskCompletionSource<bool> readyToConsume) : IWorker<WorkQueuesEvent> {
	int consumed = 0;
	public int ConsumedCount => consumed;
	public TaskCompletionSource<bool> ReadyToConsume { get; } = readyToConsume;
	public TaskCompletionSource<bool> OnChannelClose { get; } = new ();

	public bool UseBackgroundThread => false;
	public async Task ConsumeAsync (WorkQueuesEvent message, CancellationToken token = default)
	{
		// use this as a way to block the worker
		await ReadyToConsume.Task;
		// increase the count in a thread safe way
		Interlocked.Increment(ref consumed);
	}

	public Task OnChannelClosedAsync (string channelName, CancellationToken token = default)
	{
		OnChannelClose.TrySetResult (true);
		return Task.CompletedTask;
	}

	public void Dispose () { }

	public ValueTask DisposeAsync () => ValueTask.CompletedTask;
}
