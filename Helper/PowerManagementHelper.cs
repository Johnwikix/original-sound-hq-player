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

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(
            uint processAccess,
            bool bInheritHandle,
            int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetPriorityClass(
            IntPtr hProcess,
            uint dwPriorityClass);

        [DllImport("kernel32.dll")]
        private static extern uint GetPriorityClass(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

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

        // 电源管理常量
        private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
        private const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

        // 进程优先级常量
        private const uint PROCESS_SET_INFORMATION = 0x0200;
        private const uint NORMAL_PRIORITY_CLASS = 0x0020;
        private const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x8000;
        private const uint HIGH_PRIORITY_CLASS = 0x0080;
        private const uint IDLE_PRIORITY_CLASS = 0x0040;
        private const uint BELOW_NORMAL_PRIORITY_CLASS = 0x4000;
        private const uint REALTIME_PRIORITY_CLASS = 0x0100;

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
        /// 设置当前进程的优先级
        /// </summary>
        /// <param name="priorityClass">要设置的优先级</param>
        /// <returns>操作是否成功</returns>
        public static bool SetProcessPriority(ProcessPriorityClass priorityClass)
        {
            try
            {
                uint priorityValue = GetPriorityValue(priorityClass);

                int currentProcessId = Process.GetCurrentProcess().Id;
                IntPtr processHandle = OpenProcess(PROCESS_SET_INFORMATION, false, currentProcessId);

                if (processHandle == IntPtr.Zero)
                {
                    Debug.WriteLine("无法获取进程句柄");
                    return false;
                }

                bool result = SetPriorityClass(processHandle, priorityValue);
                CloseHandle(processHandle);

                if (result)
                {
                    Debug.WriteLine($"成功设置进程优先级为: {priorityClass}");
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    Debug.WriteLine($"设置进程优先级失败，错误代码: {error}");
                }

                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"尝试设置进程优先级时出错: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取当前进程的优先级
        /// </summary>
        /// <returns>当前进程的优先级</returns>
        public static ProcessPriorityClass GetCurrentProcessPriority()
        {
            try
            {
                int currentProcessId = Process.GetCurrentProcess().Id;
                IntPtr processHandle = OpenProcess(PROCESS_SET_INFORMATION, false, currentProcessId);

                if (processHandle == IntPtr.Zero)
                {
                    Debug.WriteLine("无法获取进程句柄");
                    return ProcessPriorityClass.Normal;
                }

                uint priorityValue = GetPriorityClass(processHandle);
                CloseHandle(processHandle);

                return GetPriorityClass(priorityValue);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取进程优先级时出错: {ex.Message}");
                return ProcessPriorityClass.Normal;
            }
        }

        /// <summary>
        /// 获取优先级类对应的Win32 API值
        /// </summary>
        private static uint GetPriorityValue(ProcessPriorityClass priorityClass)
        {
            return priorityClass switch
            {
                ProcessPriorityClass.Idle => IDLE_PRIORITY_CLASS,
                ProcessPriorityClass.BelowNormal => BELOW_NORMAL_PRIORITY_CLASS,
                ProcessPriorityClass.Normal => NORMAL_PRIORITY_CLASS,
                ProcessPriorityClass.AboveNormal => ABOVE_NORMAL_PRIORITY_CLASS,
                ProcessPriorityClass.High => HIGH_PRIORITY_CLASS,
                ProcessPriorityClass.RealTime => REALTIME_PRIORITY_CLASS,
                _ => NORMAL_PRIORITY_CLASS
            };
        }

        /// <summary>
        /// 从Win32 API值获取优先级类
        /// </summary>
        private static ProcessPriorityClass GetPriorityClass(uint priorityValue)
        {
            return priorityValue switch
            {
                IDLE_PRIORITY_CLASS => ProcessPriorityClass.Idle,
                BELOW_NORMAL_PRIORITY_CLASS => ProcessPriorityClass.BelowNormal,
                NORMAL_PRIORITY_CLASS => ProcessPriorityClass.Normal,
                ABOVE_NORMAL_PRIORITY_CLASS => ProcessPriorityClass.AboveNormal,
                HIGH_PRIORITY_CLASS => ProcessPriorityClass.High,
                REALTIME_PRIORITY_CLASS => ProcessPriorityClass.RealTime,
                _ => ProcessPriorityClass.Normal
            };
        }
    }

    /// <summary>
    /// 进程优先级类别
    /// </summary>
    public enum ProcessPriorityClass
    {
        /// <summary>
        /// 空闲优先级，仅在系统空闲时运行
        /// </summary>
        Idle,

        /// <summary>
        /// 低于正常优先级
        /// </summary>
        BelowNormal,

        /// <summary>
        /// 正常优先级
        /// </summary>
        Normal,

        /// <summary>
        /// 高于正常优先级
        /// </summary>
        AboveNormal,

        /// <summary>
        /// 高优先级
        /// </summary>
        High,

        /// <summary>
        /// 实时优先级（谨慎使用，可能影响系统稳定性）
        /// </summary>
        RealTime
    }
}
