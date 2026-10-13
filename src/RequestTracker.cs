using System;
using System.Collections.Generic;

namespace CodexCompletionReminder
{
    internal sealed class CardRequest
    {
        public string Kind;
        public string Id;
        public string Scope;
    }
    internal sealed class CardSnapshot
    {
        public List<CardRequest> Requests = new List<CardRequest>();
        // Only scopes whose entire request source is positively readable may be resolved.
        public HashSet<string> CompleteScopes = new HashSet<string>();
    }
    internal sealed class RequestTracker
    {
        private readonly Dictionary<string, CardRequest> waiting = new Dictionary<string, CardRequest>();
        private readonly HashSet<string> dismissed = new HashSet<string>();
        public int WaitingCount { get { return waiting.Count; } }
        public IEnumerable<CardRequest> Visible
        {
            get { foreach (KeyValuePair<string, CardRequest> p in waiting) if (!dismissed.Contains(p.Key)) yield return p.Value; }
        }
        public void Apply(CardSnapshot snapshot)
        {
            if (snapshot == null) return; // Unknown preserves all unresolved requests.
            HashSet<string> current = new HashSet<string>();
            foreach (CardRequest request in snapshot.Requests)
            {
                if (request == null || !SecureTransport.ValidId(request.Id) || !SecureTransport.ValidId(request.Scope) ||
                    request.Kind != ReminderKinds.Choice) continue;
                string key = request.Kind + ":" + request.Id;
                current.Add(key);
                if (!waiting.ContainsKey(key) && waiting.Count < 1024) waiting.Add(key, request);
            }
            List<string> resolved = new List<string>();
            foreach (KeyValuePair<string, CardRequest> p in waiting)
                if (snapshot.CompleteScopes.Contains(p.Value.Scope) && !current.Contains(p.Key)) resolved.Add(p.Key);
            foreach (string key in resolved) { waiting.Remove(key); dismissed.Remove(key); }
        }
        public int Count(string kind)
        {
            int count = 0;
            foreach (CardRequest request in Visible) if (request.Kind == kind) count++;
            return count;
        }
        public void Dismiss(string kind)
        {
            foreach (KeyValuePair<string, CardRequest> p in waiting)
                if (p.Value.Kind == kind) dismissed.Add(p.Key);
        }
        public void DismissAll()
        {
            foreach (string key in waiting.Keys) dismissed.Add(key);
        }
    }
}
