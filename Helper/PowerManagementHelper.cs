using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Helper
{
    public static class PowerManagementHelper
    {
        #region Win32 API 声明

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(
            IntPtr hProcess,
            PROCESS_INFORMATION_CLASS ProcessInformationClass,
            IntPtr ProcessInformation,
            uint ProcessInformationSize);

        #endregion

        #region 枚举和结构

        // 进程信息类
        private enum PROCESS_INFORMATION_CLASS
        {
            ProcessMemoryPriority,
            ProcessMemoryExhaustionInfo,
            ProcessAppMemoryInfo,
            ProcessInPrivateInfo,
            ProcessPowerThrottling,
            ProcessReservedValue1,
            ProcessTelemetryCoverageInfo,
            ProcessProtectionLevelInfo,
            ProcessLeapSecondInfo,
            ProcessMachineTypeInfo,
            ProcessInformationClassMax
        }

        // 电源节流设置结构
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        #endregion

        #region 常量

        private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
        private const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

        #endregion

        /// <summary>
        /// 禁用当前进程的效率模式（电源节流）
        /// </summary>
        /// <returns>操作是否成功</returns>
        public static bool DisableEfficiencyMode()
        {
            try
            {
                var throttleState = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                    StateMask = 0 // 设置为0表示禁用这些功能
                };

                int size = Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>();
                IntPtr pThrottleState = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(throttleState, pThrottleState, false);

                bool result = SetProcessInformation(
                    GetCurrentProcess(),
                    PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
                    pThrottleState,
                    (uint)size
                );

                Marshal.FreeHGlobal(pThrottleState);

                if (!result)
                {
                    int error = Marshal.GetLastWin32Error();
                    Debug.WriteLine($"禁用效率模式失败，错误代码: {error}");
                }
                else
                {
                    Debug.WriteLine("成功禁用效率模式");
                }

                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"尝试禁用效率模式时出错: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 启用当前进程的效率模式（电源节流）
        /// </summary>
        /// <returns>操作是否成功</returns>
        public static bool EnableEfficiencyMode()
        {
            try
            {
                var throttleState = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                    StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION
                };

                int size = Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>();
                IntPtr pThrottleState = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(throttleState, pThrottleState, false);

                bool result = SetProcessInformation(
                    GetCurrentProcess(),
                    PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
                    pThrottleState,
                    (uint)size
                );

                Marshal.FreeHGlobal(pThrottleState);

                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"尝试启用效率模式时出错: {ex.Message}");
                return false;
            }
        }
    }
}
