using System;
using System.Collections.Generic;
using System.Threading;

namespace HotReloadTool
{
    // Reserve before dispatch; every completion, including failure, drains the next job.
    public sealed class InstallQueue
    {
        sealed class Job { public string key; public Action run; }
        readonly object gate = new object();
        readonly Queue<Job> waiting = new Queue<Job>();
        readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool running;
        public bool Enqueue(string key, Action run, Action reserved = null)
        {
            Job first = null;
            lock (gate)
            {
                if (!pending.Add(key)) return false;
                if (reserved != null) reserved();
                var job = new Job { key = key, run = run };
                if (running) waiting.Enqueue(job);
                else { running = true; first = job; }
            }
            if (first != null) Dispatch(first);
            return true;
        }
        void Dispatch(Job job)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { job.run(); }
                catch { /* The caller reports its operation error. Continue the queue. */ }
                finally
                {
                    Job next = null;
                    lock (gate)
                    {
                        pending.Remove(job.key);
                        if (waiting.Count > 0) next = waiting.Dequeue();
                        else running = false;
                    }
                    if (next != null) Dispatch(next);
                }
            });
        }
    }
}
