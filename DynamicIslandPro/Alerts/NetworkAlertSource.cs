using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using DynamicIslandPro.Island;
using Windows.Networking.Connectivity;

namespace DynamicIslandPro.Alerts
{
    /// <summary>
    /// 网络提醒源：监听 <see cref="NetworkChange.NetworkAddressChanged"/>，
    /// 在网络连接 / 断开转换时投递提醒。
    ///
    /// 实现要点：
    /// - NetworkAddressChanged 是系统级事件，任意网卡地址变化触发（插网线、连/断 WiFi、VPN 拨号…）。
    /// - "是否在线"用 <see cref="NetworkInterface.GetIsNetworkAvailable"/>——
    ///   它判断是否有任一网卡 up 且非 loopback/tunnel，比查单网卡鲁棒。
    /// - 防刷屏：跟踪 _wasOnline，仅在 true↔false 真实转换时投递。
    /// - 连接时副标题尽量读当前 SSID（WinRT WiFiAdapter），读不到则用通用文案。
    /// - NetworkAddressChanged 可能在非 UI 线程触发；AlertHost.Enqueue 自带 Dispatcher marshal。
    /// </summary>
    public sealed class NetworkAlertSource : IDisposable
    {
        private readonly AlertHost _host;
        private bool _started;
        private bool _wasOnline;
        private bool _disposed;

        public NetworkAlertSource(AlertHost host) => _host = host;

        public void Start()
        {
            if (_started) return;
            _started = true;

            // 记录初始状态，避免启动即误报"已连接"
            _wasOnline = NetworkInterface.GetIsNetworkAvailable();
            NetworkChange.NetworkAddressChanged += OnAddressChanged;
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        }

        private void OnAddressChanged(object? sender, EventArgs e)
        {
            if (!DisplaySettings.Instance.EnableNetworkAlert) return;

            bool online = NetworkInterface.GetIsNetworkAvailable();

            // 连接：false→true。读 SSID 是异步的，先把"已连接"投出去，名字到了再补一条更新太啰嗦，
            // 这里用 fire-and-forget task 读名字——读到了把当前提醒替换成带名字的版本。
            if (online && !_wasOnline)
            {
                _host.Enqueue(new SimpleAlert(
                    "network.up", "网络已连接", "已接入网络", "📶",
                    TimeSpan.FromSeconds(2.5), priority: 25));
                _ = TryEnrichConnectedAsync();
            }
            // 断开：true→false
            else if (!online && _wasOnline)
            {
                _host.Enqueue(new SimpleAlert(
                    "network.down", "网络已断开", "请检查网络连接", "🌐",
                    TimeSpan.FromSeconds(2.5), priority: 60));
            }

            _wasOnline = online;
        }

        /// <summary>
        /// 尝试用 WinRT NetworkInformation 读当前连接的 SSID，把"网络已连接"提醒的副标题补成 WiFi 名。
        /// GetConnectionProfiles 返回活动连接配置；WiFi 连接的 ProfileName 即 SSID（如"Home"）。
        /// 读不到（用网线 / 无 WiFi / 权限不足）则什么都不做——通用文案已够用。
        /// </summary>
        private async Task TryEnrichConnectedAsync()
        {
            // 切到线程池：WinRT 调用避免阻塞 NetworkAddressChanged 回调线程
            await Task.Run(() =>
            {
                try
                {
                    // GetInternetConnectionProfile 返回当前承载 Internet 的连接（最准）
                    var profile = NetworkInformation.GetInternetConnectionProfile();
                    if (profile?.WlanConnectionProfileDetails == null) return;

                    var ssid = profile.WlanConnectionProfileDetails.GetConnectedSsid();
                    if (string.IsNullOrWhiteSpace(ssid)) return;

                    // 投递一条带名字的"更新版"连接提醒。原通用版可能在队列里或正在展示，
                    // 同优先级不抢占（>才打断），故通用版展示完这条才上，
                    // 体验上像"先连上 → 显示具体 WiFi 名"，可接受。
                    _host.Enqueue(new SimpleAlert(
                        "network.up.ssid", "已连接 WiFi", ssid, "📶",
                        TimeSpan.FromSeconds(2.5), priority: 25));
                }
                catch
                {
                    // WinRT 不可用 / 读不到——静默，通用文案已足够
                }
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
