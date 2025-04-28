﻿using Masuit.Tools.Logging;
using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Masuit.Tools.Abstractions.Hardware;

namespace Masuit.Tools.Hardware
{
    /// <summary>
    /// 硬件信息，部分功能需要C++支持，仅支持Windows系统
    /// </summary>
    public static partial class SystemInfo
    {
        #region 字段

        private const int GwHwndfirst = 0;
        private const int GwHwndnext = 2;
        private const int GwlStyle = -16;
        private const int WsVisible = 268435456;
        private const int WsBorder = 8388608;
        private static readonly string[] InstanceNames = [];
        private static readonly Dictionary<string, dynamic> Cache = new();
        private static readonly ConcurrentDictionary<string, PerformanceCounter> Counters = [];
        public static bool IsWinPlatform => Environment.OSVersion.Platform is PlatformID.Win32Windows or PlatformID.Win32S or PlatformID.WinCE or PlatformID.Win32NT;

        #endregion 字段

        #region 构造函数

        /// <summary>
        /// 静态构造函数
        /// </summary>
        static SystemInfo()
        {
            if (IsWinPlatform)
            {
                //初始化CPU计数器
                Counters["CpuCounter"] = new PerformanceCounter("Processor", "% Processor Time", "_Total")
                {
                    MachineName = "."
                };
                Counters["CpuCounter"].NextValue();

                //获得物理内存
                try
                {
                    using var mc = new ManagementClass("Win32_ComputerSystem");
                    using var moc = mc.GetInstances();
                    foreach (var mo in moc)
                    {
                        using (mo)
                        {
                            if (mo["TotalPhysicalMemory"] != null)
                            {
                                PhysicalMemory = mo["TotalPhysicalMemory"].ChangeTypeTo<long>();
                                break;
                            }
                        }
                    }

                    var cat = new PerformanceCounterCategory("Network Interface");
                    InstanceNames = cat.GetInstanceNames();
                }
                catch (Exception e)
                {
                    LogManager.Error(e);
                }
            }

            //CPU个数
            ProcessorCount = Environment.ProcessorCount;
        }

        #endregion 构造函数

        private static bool CompactFormat { get; set; }

        #region CPU相关

        /// <summary>
        /// 获取CPU核心数
        /// </summary>
        public static int ProcessorCount { get; }

        /// <summary>
        /// 获取CPU占用率 %
        /// </summary>
        public static float CpuLoad
        {
            get
            {
                if (!IsWinPlatform) return 0;
                return Counters["CpuCounter"].NextValue();
            }
        }

        /// <summary>
        /// 获取当前进程的CPU使用率（至少需要0.5s）
        /// </summary>
        /// <returns></returns>
        public static async Task<double> GetCpuUsageForProcess(CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            using var p1 = Process.GetCurrentProcess();
            var startCpuUsage = p1.TotalProcessorTime;
            await Task.Delay(500, cancellationToken);
            var endTime = DateTime.UtcNow;
            using var p2 = Process.GetCurrentProcess();
            var endCpuUsage = p2.TotalProcessorTime;
            var cpuUsedMs = (endCpuUsage - startCpuUsage).TotalMilliseconds;
            var totalMsPassed = (endTime - startTime).TotalMilliseconds;
            return cpuUsedMs / (Environment.ProcessorCount * totalMsPassed) * 100;
        }

        /// <summary>
        /// 获取CPU温度
        /// </summary>
        /// <returns>CPU温度</returns>
        public static float GetCPUTemperature()
        {
            if (!IsWinPlatform) return 0;

            try
            {
                using var mos = new ManagementObjectSearcher(@"root\WMI", "select * from MSAcpi_ThermalZoneTemperature");
                using var moc = mos.Get();
                foreach (var mo in moc)
                {
                    using (mo)
                    {
                        //这就是CPU的温度了
                        var temp = (mo["CurrentTemperature"].ChangeTypeTo<float>() - 2732) / 10;
                        return (float)Math.Round(temp, 2);
                    }
                }

                return 0;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// 获取CPU的数量
        /// </summary>
        /// <returns>CPU的数量</returns>
        public static int GetCpuCount()
        {
            try
            {
                return Cache.GetOrAdd(nameof(GetCpuCount), () =>
                {
                    if (!IsWinPlatform)
                    {
                        return Environment.ProcessorCount;
                    }

                    using var m = new ManagementClass("Win32_Processor");
                    using var moc = m.GetInstances();
                    return moc.Count;
                });
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static readonly Lazy<List<ManagementBaseObject>> CpuObjects = new(() =>
        {
            using var mos = new ManagementObjectSearcher("SELECT * FROM Win32_Processor");
            using var moc = mos.Get();
            return moc.AsParallel().Cast<ManagementBaseObject>().ToList();
        });

        /// <summary>
        /// 获取CPU信息
        /// </summary>
        /// <returns>CPU信息</returns>
        public static List<CpuInfo> GetCpuInfo()
        {
            try
            {
                if (!IsWinPlatform) return [];
                return CpuObjects.Value.Select(mo => new CpuInfo
                {
                    NumberOfLogicalProcessors = ProcessorCount,
                    CurrentClockSpeed = mo["CurrentClockSpeed"]?.ToString(),
                    Manufacturer = mo["Manufacturer"]?.ToString(),
                    MaxClockSpeed = mo["MaxClockSpeed"]?.ToString(),
                    Type = mo["Name"]?.ToString(),
                    DataWidth = mo["DataWidth"]?.ToString(),
                    SerialNumber = mo["ProcessorId"]?.ToString(),
                    DeviceID = mo["DeviceID"]?.ToString(),
                    NumberOfCores = mo["NumberOfCores"].ChangeTypeTo<int>()
                }).ToList();
            }
            catch (Exception e)
            {
                return [new CpuInfo
                    {
                        DeviceID = null,
                        Type = e.Message,
                        Manufacturer = null,
                        MaxClockSpeed = null,
                        CurrentClockSpeed = null,
                        NumberOfCores = 0,
                        NumberOfLogicalProcessors = 0,
                        DataWidth = null,
                        SerialNumber = null
                    }
                ];
            }
        }

#if NET5_0_OR_GREATER
        public static string GetCpuId()
        {
            if (System.Runtime.Intrinsics.X86.X86Base.IsSupported)
            {
                var (eax, ebx, ecx, edx) = System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0);
                return edx.ToString("X").PadLeft(8, '0') + eax.ToString("X").PadLeft(8, '0');
            }
            return null;
        }
#endif

        /// <summary>
        /// 获取进程的实例名称
        /// </summary>
        /// <param name="p"></param>
        /// <returns></returns>
        public static string GetInstanceName(this Process p)
        {
            try
            {
                var pcc = new PerformanceCounterCategory("Process");
                var instances = pcc.GetInstanceNames();
                foreach (string instance in instances)
                {
                    var counter = Counters.GetOrAdd(nameof(instance) + instance, () => new PerformanceCounter("Process", "ID Process", instance));
                    if (Math.Abs(counter.NextValue() - p.Id) < 1e-8)
                    {
                        return instance;
                    }
                }
            }
            catch (Exception ex)
            {
            }
            return null;
        }

        /// <summary>
        /// 获取进程的CPU使用率
        /// </summary>
        /// <returns></returns>
        public static float GetProcessCpuUsage(int pid)
        {
            using var process = Process.GetProcessById(pid);
            return GetProcessCpuUsage(process);
        }

        /// <summary>
        /// 获取进程的CPU使用率
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public static IEnumerable<(Process process, float usage)> GetProcessCpuUsage(string name)
        {
            var processes = Process.GetProcessesByName(name);
            return GetProcessCpuUsage(processes);
        }

        /// <summary>
        /// 获取进程的CPU使用率
        /// </summary>
        /// <param name="processes"></param>
        /// <returns></returns>
        public static IEnumerable<(Process process, float usage)> GetProcessCpuUsage(this Process[] processes)
        {
            return processes.Select(p => (p, GetProcessCpuUsage(p)));
        }

        /// <summary>
        /// 获取进程的CPU使用率
        /// </summary>
        /// <param name="process"></param>
        /// <returns></returns>
        public static float GetProcessCpuUsage(this Process process)
        {
            if (!IsWinPlatform) return 0;
            string instance = GetInstanceName(process);
            if (instance != null)
            {
                var cpuCounter = Counters.GetOrAdd("Processor Time" + instance, () =>
                {
                    var counter = new PerformanceCounter("Process", "% Processor Time", instance);
                    counter.NextValue();
                    return counter;
                });
                //Thread.Sleep(200); //等200ms(是测出能换取下个样本的最小时间间隔)，让后系统获取下一个样本,因为第一个样本无效
                return cpuCounter.NextValue() / Environment.ProcessorCount;
            }
            return 0;
        }

        #endregion CPU相关

        #region 内存相关

        /// <summary>
        /// 获取可用内存(单位：Byte)
        /// </summary>
        public static long MemoryAvailable
        {
            get
            {
                if (!IsWinPlatform) return 0;

                try
                {
                    using var mc = new ManagementClass("Win32_OperatingSystem");
                    using var moc = mc.GetInstances();
                    foreach (var mo in moc)
                    {
                        using (mo)
                        {
                            if (mo["FreePhysicalMemory"] != null)
                            {
                                return 1024 * mo["FreePhysicalMemory"].ChangeTypeTo<long>();
                            }
                        }
                    }

                    return 0;
                }
                catch (Exception)
                {
                    return -1;
                }
            }
        }

        /// <summary>
        /// 获取物理内存(单位：Byte)
        /// </summary>
        public static long PhysicalMemory { get; }

        /// <summary>
        /// 获取内存信息
        /// </summary>
        /// <returns>内存信息</returns>
        public static RamInfo GetRamInfo()
        {
            return new RamInfo
            {
                MemoryAvailable = GetFreePhysicalMemory(),
                PhysicalMemory = GetTotalPhysicalMemory(),
                TotalPageFile = GetTotalVirtualMemory(),
                AvailablePageFile = GetTotalVirtualMemory() - GetUsedVirtualMemory(),
                AvailableVirtual = 1 - GetUsageVirtualMemory(),
                TotalVirtual = 1 - GetUsedPhysicalMemory()
            };
        }

        /// <summary>
        /// 获取虚拟内存使用率详情
        /// </summary>
        /// <returns></returns>
        public static string GetMemoryVData()
        {
            if (!IsWinPlatform) return "";
            float d = GetCounterValue("Memory", "% Committed Bytes In Use", null);
            var str = d.ToString("F") + "% (";
            d = GetCounterValue("Memory", "Committed Bytes", null);
            str += FormatBytes(d) + " / ";
            d = GetCounterValue("Memory", "Commit Limit", null);
            return str + FormatBytes(d) + ") ";
        }

        /// <summary>
        /// 获取虚拟内存使用率
        /// </summary>
        /// <returns></returns>
        public static float GetUsageVirtualMemory()
        {
            return GetCounterValue("Memory", "% Committed Bytes In Use", null);
        }

        /// <summary>
        /// 获取虚拟内存已用大小
        /// </summary>
        /// <returns></returns>
        public static float GetUsedVirtualMemory()
        {
            return GetCounterValue("Memory", "Committed Bytes", null);
        }

        /// <summary>
        /// 获取虚拟内存总大小
        /// </summary>
        /// <returns></returns>
        public static float GetTotalVirtualMemory()
        {
            return GetCounterValue("Memory", "Commit Limit", null);
        }

        /// <summary>
        /// 获取物理内存使用率详情描述
        /// </summary>
        /// <returns></returns>
        public static string GetMemoryPData()
        {
            if (!IsWinPlatform) return "";
            string s = QueryComputerSystem("totalphysicalmemory");
            if (string.IsNullOrEmpty(s)) return "";

            var totalphysicalmemory = Convert.ToSingle(s);
            var d = GetCounterValue("Memory", "Available Bytes", null);
            d = totalphysicalmemory - d;
            s = CompactFormat ? "%" : "% (" + FormatBytes(d) + " / " + FormatBytes(totalphysicalmemory) + ")";
            d /= totalphysicalmemory;
            d *= 100;
            return CompactFormat ? (int)d + s : d.ToString("F") + s;
        }

        /// <summary>
        /// 获取物理内存总数，单位B
        /// </summary>
        /// <returns></returns>
        public static float GetTotalPhysicalMemory()
        {
            return Cache.GetOrAdd(nameof(GetTotalPhysicalMemory), () =>
            {
                var s = QueryComputerSystem("totalphysicalmemory");
                return s.TryConvertTo<float>();
            });
        }

        /// <summary>
        /// 获取空闲的物理内存数，单位B
        /// </summary>
        /// <returns></returns>
        public static float GetFreePhysicalMemory()
        {
            return GetCounterValue("Memory", "Available Bytes", null);
        }

        /// <summary>
        /// 获取已经使用了的物理内存数，单位B
        /// </summary>
        /// <returns></returns>
        public static float GetUsedPhysicalMemory()
        {
            return GetTotalPhysicalMemory() - GetFreePhysicalMemory();
        }

        /// <summary>
        /// 获取进程的内存使用量，单位：MB
        /// </summary>
        /// <returns></returns>
        public static float GetProcessMemory(int pid)
        {
            using var process = Process.GetProcessById(pid);
            return GetProcessMemory(process);
        }

        /// <summary>
        /// 获取进程的内存使用量，单位：MB
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public static IEnumerable<(Process process, float usage)> GetProcessMemory(string name)
        {
            var processes = Process.GetProcessesByName(name);
            return GetProcessMemory(processes);
        }

        /// <summary>
        /// 获取进程的内存使用量，单位：MB
        /// </summary>
        /// <param name="processes"></param>
        /// <returns></returns>
        public static IEnumerable<(Process process, float usage)> GetProcessMemory(this Process[] processes)
        {
            return processes.Select(p => (p, GetProcessMemory(p)));
        }

        /// <summary>
        /// 获取进程的内存使用量，单位：MB
        /// </summary>
        /// <param name="process"></param>
        /// <returns></returns>
        public static float GetProcessMemory(this Process process)
        {
            if (!IsWinPlatform) return 0;
            string instance = GetInstanceName(process);
            if (instance != null)
            {
                var ramCounter = Counters.GetOrAdd("Working Set" + instance, () => new PerformanceCounter("Process", "Working Set", instance));
                var mb = ramCounter.NextValue() / 1024 / 1024;
                return mb;
            }
            return 0;
        }

        public static long CurrentProcessMemory
        {
            get
            {
                using var process = Process.GetCurrentProcess();
                return (long)GetCounterValue("Process", "Working Set - Private", process.ProcessName);
            }
        }

        #endregion 内存相关

        #region 硬盘相关

        /// <summary>
        /// 获取硬盘的读写速率
        /// </summary>
        /// <param name="dd">读或写</param>
        /// <returns></returns>
        public static float GetDiskData(DiskData dd)
        {
            return dd switch
            {
                DiskData.Read => GetCounterValue("PhysicalDisk", "Disk Read Bytes/sec", "_Total"),
                DiskData.Write => GetCounterValue("PhysicalDisk", "Disk Write Bytes/sec", "_Total"),
                DiskData.ReadAndWrite => GetCounterValue("PhysicalDisk", "Disk Read Bytes/sec", "_Total") + GetCounterValue("PhysicalDisk", "Disk Write Bytes/sec", "_Total"),
                _ => 0
            };
        }

        private static List<DiskInfo> _diskInfos = [];

        /// <summary>
        /// 获取磁盘可用空间
        /// </summary>
        /// <returns></returns>
        public static List<DiskInfo> GetDiskInfo()
        {
            try
            {
                if (!IsWinPlatform || _diskInfos.Count > 0)
                {
                    return _diskInfos;
                }

                using var mc = new ManagementClass("Win32_DiskDrive");
                using var moc = mc.GetInstances();
                var list = new List<DiskInfo>();
                foreach (var mo in moc)
                {
                    using (mo)
                    {
                        list.Add(new DiskInfo()
                        {
                            Index = mo["Index"].ChangeTypeTo<int>(),
                            Total = mo["Size"].ChangeTypeTo<long>(),
                            Model = mo["Model"].ToString(),
                            MediaType = mo["MediaType"].ToString(),
                            SerialNumber = mo["SerialNumber"].ToString(),
                        });
                    }
                }

                _diskInfos = list.OrderBy(x => x.Index).ToList();
                return _diskInfos;
            }
            catch (Exception)
            {
                return [];
            }
        }

        #endregion 硬盘相关

        #region 网络相关

        /// <summary>
        /// 获取网络的传输速率
        /// </summary>
        /// <param name="nd">上传或下载</param>
        /// <returns></returns>
        public static float GetNetData(NetData nd)
        {
            if (!IsWinPlatform) return 0;
            if (InstanceNames is { Length: 0 }) return 0;

            float d = 0;
            for (int i = 0; i < InstanceNames.Length; i++)
            {
                float receied = GetCounterValue("Network Interface", "Bytes Received/sec", InstanceNames[i]);
                float send = GetCounterValue("Network Interface", "Bytes Sent/sec", InstanceNames[i]);
                switch (nd)
                {
                    case NetData.Received:
                        d += receied;
                        break;

                    case NetData.Sent:
                        d += send;
                        break;

                    case NetData.ReceivedAndSent:
                        d += receied + send;
                        break;

                    default:
                        d += 0;
                        break;
                }
            }

            return d;
        }

        /// <summary>
        /// 获取网卡硬件地址
        /// </summary>
        /// <returns></returns>
        public static IEnumerable<PhysicalAddress> GetMacAddress()
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(c => c.NetworkInterfaceType != NetworkInterfaceType.Loopback && c.OperationalStatus == OperationalStatus.Up && c.GetIPProperties().UnicastAddresses.Any(temp => temp.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)).Select(c => c.GetPhysicalAddress());
        }

        /// <summary>
        /// 获取IP地址WMI
        /// </summary>
        /// <returns></returns>
        public static string GetIPAddressWMI()
        {
            try
            {
                if (!IsWinPlatform) return "";

                return Cache.GetOrAdd(nameof(GetIPAddressWMI), () =>
                {
                    using var mc = new ManagementClass("Win32_NetworkAdapterConfiguration");
                    using var moc = mc.GetInstances();
                    foreach (var mo in moc)
                    {
                        if ((bool)mo["IPEnabled"])
                        {
                            return ((string[])mo["IpAddress"])[0];
                        }
                    }

                    return "";
                });
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }

            return "";
        }

        /// <summary>
        /// 获取当前使用的IP
        /// </summary>
        /// <returns></returns>
        public static IPAddress GetLocalUsedIP()
        {
            return GetLocalUsedIP(AddressFamily.InterNetwork);
        }

        /// <summary>
        /// 获取当前使用的IP
        /// </summary>
        /// <returns></returns>
        public static IPAddress GetLocalUsedIP(AddressFamily family)
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(c => c.NetworkInterfaceType != NetworkInterfaceType.Loopback && c.OperationalStatus == OperationalStatus.Up).OrderByDescending(c => c.Speed).Select(t => t.GetIPProperties()).Where(p => p.DhcpServerAddresses.Count > 0).SelectMany(p => p.UnicastAddresses).Select(p => p.Address).FirstOrDefault(p => !(p.IsIPv6Teredo || p.IsIPv6LinkLocal || p.IsIPv6Multicast || p.IsIPv6SiteLocal) && p.AddressFamily == family);
        }

        /// <summary>
        /// 获取本机所有的ip地址
        /// </summary>
        /// <returns></returns>
        public static List<UnicastIPAddressInformation> GetLocalIPs()
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces().Where(c => c.NetworkInterfaceType != NetworkInterfaceType.Loopback && c.OperationalStatus == OperationalStatus.Up).OrderByDescending(c => c.Speed); //所有网卡信息
            return interfaces.SelectMany(n => n.GetIPProperties().UnicastAddresses).ToList();
        }

        /// <summary>
        /// 获取网卡地址
        /// </summary>
        /// <returns></returns>
        public static string GetNetworkCardAddress()
        {
            try
            {
                if (!IsWinPlatform) return "";

                return Cache.GetOrAdd(nameof(GetNetworkCardAddress), () =>
                {
                    using var mos = new ManagementObjectSearcher("select * from Win32_NetworkAdapter where ((MACAddress Is Not NULL) and (Manufacturer <> 'Microsoft'))");
                    using var moc = mos.Get();
                    foreach (var mo in moc)
                    {
                        return mo["MACAddress"].ToString().Trim();
                    }

                    return "";
                });
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }

            return "";
        }

        #endregion 网络相关

        #region 系统相关

        /// <summary>
        /// 获取计算机开机时间
        /// </summary>
        /// <returns>datetime</returns>
        public static DateTime BootTime()
        {
            if (!IsWinPlatform) return default;

            var query = new SelectQuery("SELECT LastBootUpTime FROM Win32_OperatingSystem WHERE Primary='true'");
            using var searcher = new ManagementObjectSearcher(query);
            using var moc = searcher.Get();
            foreach (var mo in moc)
            {
                using (mo)
                {
                    return ManagementDateTimeConverter.ToDateTime(mo["LastBootUpTime"].ToString());
                }
            }

            return DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount & int.MaxValue);
        }

        /// <summary>
        /// 查询计算机系统信息
        /// </summary>
        /// <param name="type">类型名</param>
        /// <returns></returns>
        public static string QueryComputerSystem(string type)
        {
            try
            {
                if (!IsWinPlatform) return string.Empty;

                var mos = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
                using var moc = mos.Get();
                foreach (var mo in moc)
                {
                    using (mo)
                    {
                        return mo[type].ToString();
                    }
                }
            }
            catch (Exception e)
            {
                return "未能获取到当前计算机系统信息，可能是当前程序无管理员权限，如果是web应用程序，请将应用程序池的高级设置中的进程模型下的标识设置为：LocalSystem；如果是普通桌面应用程序，请提升管理员权限后再操作。异常信息：" + e.Message;
            }

            return string.Empty;
        }

        /// <summary>
        /// 查找所有应用程序标题
        /// </summary>
        /// <param name="handle">应用程序标题范型</param>
        /// <returns>所有应用程序集合</returns>
        public static List<string> FindAllApps(int handle)
        {
            if (!IsWinPlatform) return new List<string>(0);

            var apps = new List<string>();
            int hwCurr = GetWindow(handle, GwHwndfirst);
            while (hwCurr > 0)
            {
                int IsTask = WsVisible | WsBorder;
                int lngStyle = GetWindowLongA(hwCurr, GwlStyle);
                bool taskWindow = (lngStyle & IsTask) == IsTask;
                if (taskWindow)
                {
                    int length = GetWindowTextLength(new IntPtr(hwCurr));
                    var sb = new StringBuilder(2 * length + 1);
                    GetWindowText(hwCurr, sb, sb.Capacity);
                    string strTitle = sb.ToString();
                    if (!string.IsNullOrEmpty(strTitle))
                    {
                        apps.Add(strTitle);
                    }
                }

                hwCurr = GetWindow(hwCurr, GwHwndnext);
            }

            return apps;
        }

        /// <summary>
        /// 操作系统类型
        /// </summary>
        /// <returns></returns>
        public static string GetSystemType()
        {
            try
            {
                return Cache.GetOrAdd(nameof(GetSystemType), () =>
                {
                    if (!IsWinPlatform)
                    {
                        return Environment.OSVersion.Platform.ToString();
                    }

                    using var mc = new ManagementClass("Win32_ComputerSystem");
                    using var moc = mc.GetInstances();
                    foreach (var mo in moc)
                    {
                        return mo["SystemType"].ToString().Trim();
                    }

                    return "";
                });
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }

            return "";
        }

        #endregion 系统相关

        #region 主板相关

        /// <summary>
        /// 获取主板序列号
        /// </summary>
        /// <returns></returns>
        public static string GetBiosSerialNumber()
        {
            try
            {
                if (!IsWinPlatform) return "";

                return Cache.GetOrAdd(nameof(GetBiosSerialNumber), () =>
                {
                    using var searcher = new ManagementObjectSearcher("select * from Win32_BIOS");
                    using var mos = searcher.Get();
                    foreach (var mo in mos)
                    {
                        return mo["SerialNumber"].ToString().Trim();
                    }

                    return "";
                });
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }

            return "";
        }

        /// <summary>
        /// 主板编号
        /// </summary>
        /// <returns></returns>
        public static BiosInfo GetBiosInfo()
        {
            if (!IsWinPlatform) return new BiosInfo();

            return Cache.GetOrAdd(nameof(GetBiosInfo), () =>
            {
                using var searcher = new ManagementObjectSearcher("select * from Win32_BaseBoard");
                using var mos = searcher.Get();
                using var reg = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var guidKey = reg.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                using var uuidKey = reg.OpenSubKey(@"SYSTEM\HardwareConfig");
                string guid = null;
                string uuid = null;
                string model = null;
                if (guidKey != null) guid = guidKey.GetValue("MachineGuid") + "";
                if (uuidKey != null) uuid = (uuidKey.GetValue("LastConfig") + "").Trim('{', '}').ToUpper();
                var biosKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                biosKey ??= Registry.LocalMachine.OpenSubKey(@"SYSTEM\HardwareConfig\Current");
                if (biosKey != null)
                {
                    model = (biosKey.GetValue("SystemProductName") + "").Replace("System Product Name", null);
                    if (model.IsNullOrEmpty()) model = biosKey.GetValue("BaseBoardProduct") + "";
                    biosKey.Dispose();
                }

                foreach (var mo in mos)
                {
                    return new BiosInfo
                    {
                        Manufacturer = mo["Manufacturer"].ToString(),
                        ID = mo["SerialNumber"].ToString(),
                        Model = model,
                        SerialNumber = GetBiosSerialNumber(),
                        Guid = guid,
                        UUID = uuid
                    };
                }

                return new BiosInfo();
            });
        }

        #endregion 主板相关

        #region 公共函数

        /// <summary>
        /// 将速度值格式化成字节单位
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
        public static string FormatBytes(this double bytes)
        {
            int unit = 0;
            while (bytes > 1024)
            {
                bytes /= 1024;
                ++unit;
            }

            string s = CompactFormat ? ((int)bytes).ToString() : bytes.ToString("F") + " ";
            return s + (Unit)unit;
        }

        private static float GetCounterValue(string categoryName, string counterName, string instanceName)
        {
            if (!IsWinPlatform) return 0;
            var counter = Counters.GetOrAdd(categoryName + ":" + counterName + ":" + instanceName, () => new PerformanceCounter(categoryName, counterName, instanceName));
            return counter.NextValue();
        }

        #endregion 公共函数

        #region Win32API声明

#pragma warning disable 1591

        [DllImport("User32")]
        public static extern int GetWindow(int hWnd, int wCmd);

        [DllImport("User32")]
        public static extern int GetWindowLongA(int hWnd, int wIndx);

        [DllImport("user32.dll")]
        public static extern bool GetWindowText(int hWnd, StringBuilder title, int maxBufSize);

        [DllImport("user32", CharSet = CharSet.Auto)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

#pragma warning restore 1591

        #endregion Win32API声明

        #region AIDA64

        /// <summary>
        /// 获取AIDA64传感器值，需要AIDA64开启共享内存
        /// </summary>
        /// <returns></returns>
        public static IEnumerable<SensorValue> GetAida64Values()
        {
            using var mmf = MemoryMappedFile.OpenExisting("AIDA64_SensorValues");
            using var stream = mmf.CreateViewStream();
            using BinaryReader binReader = new BinaryReader(stream);
            var sb = new StringBuilder((int)stream.Length);
            sb.Append("<root>");
            var c = binReader.ReadChar();
            while (c != '\0')
            {
                sb.Append(c);
                c = binReader.ReadChar();
            }
            sb.Append("</root>");
            var sharedMemString = sb.ToString();
            var document = XDocument.Parse(sharedMemString);
            foreach (var element in document.Root.Elements())
            {
                var v = new SensorValue
                {
                    Type = SensorTypeStrings.GetTypeFromStringCode(element.Name.LocalName)
                };

                foreach (var childElement in element.Elements())
                {
                    if (childElement.Name.LocalName == "id")
                    {
                        v.Identifier = childElement.Value;
                    }
                    else if (childElement.Name.LocalName == "label")
                    {
                        v.Name = childElement.Value;
                    }
                    else if (childElement.Name.LocalName == "value")
                    {
                        v.Value = childElement.Value;
                    }
                }

                yield return v;
            }
        }

        #endregion AIDA64
    }
}