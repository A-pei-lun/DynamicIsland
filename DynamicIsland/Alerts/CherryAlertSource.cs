using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DynamicIsland.Island;

namespace DynamicIsland.Alerts
{
    /// <summary>
    /// Cherry Studio 工作通知源：轮询本地 Cherry Studio API（默认 http://127.0.0.1:23333），
    /// 在 agent 产出新的 assistant 响应（一轮完成）时投递「任务完成」提醒。
    ///
    /// 检测逻辑（无 session status 字段，靠消息 role 推断）：
    /// - GET /v1/agents -> 各 agent。
    /// - GET /v1/agents/{id}/sessions -> 各 session。
    /// - GET /v1/agents/{id}/sessions/{sid}/messages -> 消息列表（role: user/assistant/tool/...）。
    /// - 每 session 记上次消息数；当消息数增加且最新一条 role=assistant -> agent 完成了一轮响应 -> 投递。
    /// - 首次轮询不投递（避免把历史当新事件）。
    ///
    /// 鉴权：Bearer Token（用户在设置里填 CherryApiKey，取自 Cherry Studio 设置）。无 key 静默不轮询。
    /// 「新消息」/「channel」通知延后；本期只做 agent 完成通知。
    /// </summary>
    public sealed class CherryAlertSource : IDisposable
    {
        private const string DefaultBaseUrl = "http://127.0.0.1:23333";
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan FirstPollDelay = TimeSpan.FromSeconds(3);

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

        private readonly AlertHost _host;
        private Timer? _timer;
        private bool _started;
        private bool _disposed;
        private string _apiKey = "";

        // agentId -> 跟踪态（名字 + 各 session 上次见到的消息数）
        private readonly Dictionary<string, AgentState> _agents = new();

        private sealed class AgentState
        {
            public string Name = "Cherry";
            public readonly Dictionary<string, int> SessionMessageCounts = new();
        }

        public CherryAlertSource(AlertHost host) => _host = host;

        public void Start()
        {
            if (_started) return;
            _started = true;
            _apiKey = DisplaySettings.Instance.CherryApiKey ?? "";
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                System.Diagnostics.Debug.WriteLine("[Cherry] 无 API Key，跳过轮询");
                return;
            }
            _timer = new Timer(_ => _ = PollAsync(), null, FirstPollDelay, PollInterval);
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        /// <summary>设置里改了 key 后重配。key 从有变无则停止轮询。</summary>
        public void Restart()
        {
            Stop();
            _agents.Clear();
            Start();
        }

        private async Task PollAsync()
        {
            if (!DisplaySettings.Instance.EnableCherryAlert) return;
            if (string.IsNullOrWhiteSpace(_apiKey)) return;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{DefaultBaseUrl}/v1/agents?limit=100");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return;
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("agents", out var agents)) return;

                foreach (var ag in agents.EnumerateArray())
                {
                    string id = ag.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
                    if (string.IsNullOrEmpty(id)) continue;
                    string name = ag.TryGetProperty("name", out var n) ? (n.GetString() ?? "Cherry") : "Cherry";
                    await PollSessionsAsync(id, name);
                }
            }
            catch
            {
                /* Cherry 未运行 / 网络错 -- 静默，下次再试 */
            }
        }

        private async Task PollSessionsAsync(string agentId, string name)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{DefaultBaseUrl}/v1/agents/{agentId}/sessions?limit=100");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return;
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("sessions", out var sessions)) return;

                if (!_agents.TryGetValue(agentId, out var st))
                {
                    st = new AgentState();
                    _agents[agentId] = st;
                }
                st.Name = name;

                foreach (var s in sessions.EnumerateArray())
                {
                    string sid = s.TryGetProperty("id", out var sidEl) ? (sidEl.GetString() ?? "") : "";
                    if (string.IsNullOrEmpty(sid)) continue;
                    await PollMessagesAsync(agentId, sid, st);
                }
            }
            catch { /* 静默 */ }
        }

        private async Task PollMessagesAsync(string agentId, string sessionId, AgentState st)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{DefaultBaseUrl}/v1/agents/{agentId}/sessions/{sessionId}/messages?limit=200");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return;
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                // 响应可能是 [...] 或 { messages: [...] }
                JsonElement msgs;
                if (doc.RootElement.ValueKind == JsonValueKind.Array) msgs = doc.RootElement;
                else if (!doc.RootElement.TryGetProperty("messages", out msgs)) return;

                int count = 0;
                string lastRole = "";
                string lastText = "";
                foreach (var m in msgs.EnumerateArray())
                {
                    count++;
                    if (m.TryGetProperty("role", out var r)) lastRole = r.GetString() ?? "";
                    if (m.TryGetProperty("content", out var c))
                    {
                        if (c.ValueKind == JsonValueKind.String) lastText = c.GetString() ?? "";
                        else if (c.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var part in c.EnumerateArray())
                                if (part.TryGetProperty("text", out var t) && !string.IsNullOrEmpty(t.GetString()))
                                    lastText = t.GetString() ?? "";
                        }
                    }
                }

                bool wasKnown = st.SessionMessageCounts.TryGetValue(sessionId, out int prevCount);
                st.SessionMessageCounts[sessionId] = count;

                // 完成信号：消息数增加 且 最新是 assistant（agent 产出了新响应）。
                // 首次见该 session 不投递（wasKnown=false，避免把历史当新事件）。
                if (wasKnown && count > prevCount && lastRole == "assistant")
                {
                    string preview = Truncate(lastText, 40);
                    _host.Enqueue(new SimpleAlert(
                        $"cherry.{sessionId}.{count}", "Cherry 任务完成",
                        string.IsNullOrEmpty(preview) ? st.Name : $"{st.Name} · {preview}",
                        "🍒", TimeSpan.FromSeconds(3.5), priority: 45, kind: AlertKind.Summary));
                }
            }
            catch { /* 静默 */ }
        }

        private static string Truncate(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
