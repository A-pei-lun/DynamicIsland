using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace DynamicIslandPro
{
    /// <summary>
    /// 开机自启工具：读写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DynamicIsland。
    ///
    /// 设计：
    /// - 注册表是唯一的 ground truth，不走 settings.json（避免两边不一致）。
    /// - HKCU（当前用户）而非 HKLM——无需管理员权限即可写。
    /// - 默认关闭：从不主动调 Enable，用户在设置里勾选才写入。
    /// - 路径用 `Process.GetCurrentProcess().MainModule.FileName`（真实 exe 全路径，
    ///   兼容用户改名/移动安装目录）；带引号防路径含空格。
    /// </summary>
    public static class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DynamicIsland";

        /// <summary>读注册表判断是否已设置自启。读不到/异常一律视为未启用。</summary>
        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                    return key?.GetValue(ValueName) is string;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>写入或删除注册表项。写失败静默——下次再试或用户报问题再查。</summary>
        public static bool SetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                if (key == null) return false;

                if (enabled)
                {
                    var path = GetExecutablePath();
                    if (string.IsNullOrEmpty(path)) return false;
                    // 路径加引号防空格；不传参数（启动行为与正常双击一致）
                    key.SetValue(ValueName, $"\"{path}\"", RegistryValueKind.String);
                }
                else
                {
                    if (key.GetValue(ValueName) != null)
                        key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>取当前进程 exe 全路径。.NET 单文件发布 / publish 时也是真实 exe。</summary>
        private static string GetExecutablePath()
        {
            try
            {
                return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
