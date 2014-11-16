namespace Dapr.WebSockets
{
    using System.Threading.Tasks;

    internal static class TaskExtensions
    {
        public static Task CompleteWith<T>(this Task task, TaskCompletionSource<T> completion)
        {
            return task.ContinueWith(
                antecedent =>
                    {
                        switch (antecedent.Status)
                        {
                            case TaskStatus.Canceled:
                                completion.TrySetCanceled();
                                break;
                            case TaskStatus.Faulted:
                                completion.TrySetException(task.Exception);
                                break;
                            case TaskStatus.RanToCompletion:
                                completion.TrySetResult(default(T));
                                break;
                        }
                    });
        }
    }
}