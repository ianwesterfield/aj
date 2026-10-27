using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using FunnelCloud.Agent.Services;
using FunnelCloud.Agent.Services.Events;
using FunnelCloud.Shared.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FunnelCloud.Agent;

/// <summary>
/// FunnelCloud Agent - Distributed execution agent for Mesosync.
/// 
/// Responsibilities:
/// 1. Listen for UDP discovery broadcasts from Mesosync
/// 2. Advertise capabilities (platform, tools, workspace roots)
/// 3. Execute tasks received over gRPC (mTLS secured)
/// 4. Return results to orchestrator
/// </summary>
public class Program
{
  public static async Task Main(string[] args)
  {
    // Kill any existing agent instances before starting
    KillExistingInstances();

    var builder = Host.CreateApplicationBuilder(args);

    // Enable Windows Service support
    builder.Services.AddWindowsService(options =>
    {
      options.ServiceName = "FunnelCloud Agent";
    });

    // Configure logging - clear defaults (including EventLog which needs System.Threading.AccessControl)
    // and explicitly add console logging only
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.SetMinimumLevel(LogLevel.Debug);

    // Build agent capabilities from environment/config
    var capabilities = BuildAgentCapabilities();
    builder.Services.AddSingleton(capabilities);

    // Register services
    builder.Services.AddSingleton<TaskExecutor>(sp =>
        new TaskExecutor(
            sp.GetRequiredService<ILogger<TaskExecutor>>(),
            capabilities.AgentId));

    // Peer registry for gossip protocol
    builder.Services.AddSingleton<PeerRegistry>(sp =>
        new PeerRegistry(
            sp.GetRequiredService<ILogger<PeerRegistry>>(),
            sp.GetRequiredService<AgentCapabilities>()));

    // Peer discovery service (used by HTTP API to discover other agents)
    builder.Services.AddSingleton<PeerDiscoveryService>(sp =>
        new PeerDiscoveryService(
            sp.GetRequiredService<ILogger<PeerDiscoveryService>>(),
            sp.GetRequiredService<AgentCapabilities>()));

    // Add discovery listener as hosted service (responds to multicast and gossip)
    builder.Services.AddHostedService<DiscoveryListener>(sp =>
        new DiscoveryListener(
            sp.GetRequiredService<ILogger<DiscoveryListener>>(),
            sp.GetRequiredService<AgentCapabilities>(),
            sp.GetRequiredService<PeerDiscoveryService>(),
            sp.GetRequiredService<PeerRegistry>()));

    // Add gossip service (periodic peer sharing)
    builder.Services.AddHostedService<GossipService>(sp =>
        new GossipService(
            sp.GetRequiredService<ILogger<GossipService>>(),
            sp.GetRequiredService<PeerRegistry>(),
            sp.GetRequiredService<PeerDiscoveryService>(),
            sp.GetRequiredService<AgentCapabilities>()));

    // Named-pipe event publisher (powers the tray UI on Windows and the
    // `funnel` CLI on Linux/Windows). Registered as a singleton so the same
    // instance is used by the hosted service, the gRPC host, TaskServiceImpl,
    // and the HTTP API (for test-event endpoint).
    builder.Services.AddSingleton<NamedPipeEventPublisher>();
    builder.Services.AddSingleton<IAgentEventPublisher>(sp =>
        sp.GetRequiredService<NamedPipeEventPublisher>());
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<NamedPipeEventPublisher>());

    // Add HTTP API server (health checks, peer discovery proxy, test events)
    builder.Services.AddHostedService<HttpApiHost>(sp =>
        new HttpApiHost(
            sp.GetRequiredService<ILogger<HttpApiHost>>(),
            sp.GetRequiredService<AgentCapabilities>(),
            sp.GetRequiredService<PeerDiscoveryService>(),
            sp.GetRequiredService<PeerRegistry>(),
            sp.GetRequiredService<IAgentEventPublisher>()));

    // Add gRPC server for task execution (mTLS secured)
    builder.Services.AddHostedService<GrpcServerHost>(sp =>
        new GrpcServerHost(
            sp.GetRequiredService<ILogger<GrpcServerHost>>(),
            sp.GetRequiredService<AgentCapabilities>(),
            sp.GetRequiredService<TaskExecutor>(),
            sp.GetRequiredService<IAgentEventPublisher>()));

    var host = builder.Build();

    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("FunnelCloud Agent starting...");
    logger.LogInformation("Agent ID: {AgentId}", capabilities.AgentId);
    logger.LogInformation("Platform: {Platform}", capabilities.Platform);
    logger.LogInformation("Capabilities: {Capabilities}", string.Join(", ", capabilities.Capabilities));
    logger.LogInformation("Workspace Roots: {WorkspaceRoots}", string.Join(", ", capabilities.WorkspaceRoots));
    logger.LogInformation("Discovery Port: {DiscoveryPort}, gRPC Port: {GrpcPort}, HTTP API Port: {HttpApiPort}",
        TrustConfig.DiscoveryPort, TrustConfig.GrpcPort, TrustConfig.HttpApiPort);

    await host.RunAsync();
  }

  private static AgentCapabilities BuildAgentCapabilities()
  {
    var agentId = Environment.GetEnvironmentVariable("FUNNEL_AGENT_ID")
        ?? Environment.MachineName.ToLowerInvariant();

    var platform = GetPlatform();
    var capabilities = DetectCapabilities();
    var workspaceRoots = GetWorkspaceRoots();
    var certFingerprint = GetCertificateFingerprint();
    var ipAddress = GetPreferredIpAddress();

    return new AgentCapabilities
    {
      AgentId = agentId,
      Hostname = Environment.MachineName,
      Platform = platform,
      Capabilities = capabilities,
      WorkspaceRoots = workspaceRoots,
      CertificateFingerprint = certFingerprint,
      IpAddress = ipAddress
    };
  }

  private static string? GetPreferredIpAddress()
  {
    try
    {
      // Get all network interfaces that are up and not loopback
      var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
          .Where(ni => ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                    && ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
          .OrderBy(ni => ni.GetIPProperties().GetIPv4Properties()?.Index ?? int.MaxValue);

      foreach (var ni in interfaces)
      {
        var props = ni.GetIPProperties();
        var ipv4 = props.UnicastAddresses
            .Where(ua => ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                      && !System.Net.IPAddress.IsLoopback(ua.Address))
            .Select(ua => ua.Address.ToString())
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(ipv4))
          return ipv4;
      }
    }
    catch { }
    return null;
  }

  private static string GetPlatform()
  {
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      return "windows";
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
      return "linux";
    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
      return "macos";
    return "unknown";
  }

  private static string[] DetectCapabilities()
  {
    var caps = new List<string>();

    // PowerShell is always available (we ship with System.Management.Automation)
    caps.Add("powershell");

    // Check for common tools
    if (IsCommandAvailable("dotnet")) caps.Add("dotnet");
    if (IsCommandAvailable("git")) caps.Add("git");
    if (IsCommandAvailable("docker")) caps.Add("docker");
    if (IsCommandAvailable("node")) caps.Add("node");
    if (IsCommandAvailable("python") || IsCommandAvailable("python3")) caps.Add("python");
    if (IsCommandAvailable("cargo")) caps.Add("rust");

    // Detect server roles based on platform
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
      caps.AddRange(DetectWindowsServerRoles());
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
      caps.AddRange(DetectLinuxServerRoles());
    }

    return caps.ToArray();
  }

  /// <summary>
  /// Detect Windows Server roles and features.
  /// Uses Get-WindowsFeature for Server editions, and service/registry checks for all editions.
  /// </summary>
  private static IEnumerable<string> DetectWindowsServerRoles()
  {
    var roles = new List<string>();

    try
    {
      // First, try Get-WindowsFeature (only works on Server editions)
      var windowsFeatures = RunPowerShellCommand(
          "Get-WindowsFeature | Where-Object { $_.Installed -eq $true } | Select-Object -ExpandProperty Name");

      if (!string.IsNullOrEmpty(windowsFeatures))
      {
        var installedFeatures = windowsFeatures
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim().ToLowerInvariant())
            .ToHashSet();

        // Map Windows Features to role names
        var featureMapping = new Dictionary<string, string>
        {
          // Active Directory
          { "ad-domain-services", "role:domain-controller" },
          { "adcs-cert-authority", "role:certificate-authority" },
          { "adfs-federation", "role:federation-services" },
          { "adlds", "role:lightweight-directory" },
          
          // Network Services
          { "dns", "role:dns-server" },
          { "dhcp", "role:dhcp-server" },
          { "npas", "role:network-policy-server" },
          { "routing", "role:routing-remote-access" },
          { "directaccess-vpn", "role:vpn-server" },
          
          // Web & Application
          { "web-server", "role:iis-web-server" },
          { "web-webserver", "role:iis-web-server" },
          { "application-server", "role:app-server" },
          
          // File & Storage
          { "fs-fileserver", "role:file-server" },
          { "fs-dfs-namespace", "role:dfs-namespace" },
          { "fs-dfs-replication", "role:dfs-replication" },
          { "fs-resource-manager", "role:file-resource-manager" },
          { "fs-nfs-service", "role:nfs-server" },
          { "fs-iscsitarget-server", "role:iscsi-target" },
          { "storage-services", "role:storage-server" },
          { "fs-data-deduplication", "role:deduplication" },
          
          // Virtualization
          { "hyper-v", "role:hypervisor" },
          { "containers", "role:container-host" },
          
          // Print Services
          { "print-server", "role:print-server" },
          { "print-internet", "role:internet-printing" },
          
          // Remote Desktop
          { "rds-rd-server", "role:remote-desktop-host" },
          { "rds-connection-broker", "role:rd-connection-broker" },
          { "rds-gateway", "role:rd-gateway" },
          { "rds-web-access", "role:rd-web-access" },
          
          // Update Services
          { "updateservices", "role:wsus" },
          { "updateservices-services", "role:wsus" },
          
          // Failover & Clustering
          { "failover-clustering", "role:failover-cluster" },
          { "nlb", "role:network-load-balancer" },
          
          // Windows Deployment
          { "wds", "role:deployment-services" },
          { "wds-deployment", "role:deployment-services" },
          
          // Fax Server
          { "fax", "role:fax-server" },
        };

        foreach (var (feature, role) in featureMapping)
        {
          if (installedFeatures.Contains(feature))
          {
            roles.Add(role);
          }
        }
      }
    }
    catch
    {
      // Get-WindowsFeature not available (likely workstation edition)
    }

    // Check for Exchange Server (registry-based detection)
    try
    {
      var exchangeVersion = RunPowerShellCommand(
          @"(Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\ExchangeServer\v15\Setup' -ErrorAction SilentlyContinue).MsiProductMajor");
      if (!string.IsNullOrWhiteSpace(exchangeVersion))
      {
        roles.Add("role:exchange-server");
      }
    }
    catch { }

    // Check for SQL Server
    try
    {
      var sqlInstances = RunPowerShellCommand(
          @"(Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server' -ErrorAction SilentlyContinue).InstalledInstances");
      if (!string.IsNullOrWhiteSpace(sqlInstances))
      {
        roles.Add("role:sql-server");
      }
    }
    catch { }

    // Service-based detection (works on all Windows editions)
    var serviceRoleMapping = new Dictionary<string, string>
    {
      { "DNS", "role:dns-server" },
      { "DHCPServer", "role:dhcp-server" },
      { "NTDS", "role:domain-controller" },  // AD DS
      { "ADWS", "role:domain-controller" },  // AD Web Services
      { "W3SVC", "role:iis-web-server" },    // IIS
      { "MSSQLSERVER", "role:sql-server" },
      { "SQLSERVERAGENT", "role:sql-server" },
      { "MSExchangeIS", "role:exchange-server" },       // Exchange Information Store
      { "MSExchangeTransport", "role:exchange-server" }, // Exchange Transport
      { "vmms", "role:hypervisor" },         // Hyper-V Management
      { "vmcompute", "role:hypervisor" },    // Hyper-V Compute
      { "docker", "role:container-host" },
      { "WinRM", "role:remote-management" },
      { "Spooler", "role:print-server" },    // Only add if Print-Server feature detected
      { "wuauserv", "role:workstation" },    // Windows Update (indicates desktop/workstation)
    };

    try
    {
      var runningServices = RunPowerShellCommand(
          "Get-Service | Where-Object { $_.Status -eq 'Running' } | Select-Object -ExpandProperty Name");

      if (!string.IsNullOrEmpty(runningServices))
      {
        var services = runningServices
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (service, role) in serviceRoleMapping)
        {
          // Don't duplicate roles already detected via features
          if (services.Contains(service) && !roles.Contains(role))
          {
            // Special case: only mark as print-server if it's actually a print server role
            if (role == "role:print-server" && !roles.Contains("role:print-server"))
              continue;

            roles.Add(role);
          }
        }
      }
    }
    catch { }

    // Detect if this is a workstation vs server
    try
    {
      var productType = RunPowerShellCommand(
          "(Get-CimInstance -ClassName Win32_OperatingSystem).ProductType");
      if (!string.IsNullOrEmpty(productType))
      {
        var type = productType.Trim();
        if (type == "1")
          roles.Add("os:workstation");
        else if (type == "2")
          roles.Add("os:domain-controller");
        else if (type == "3")
          roles.Add("os:server");
      }
    }
    catch { }

    return roles.Distinct();
  }

  /// <summary>
  /// Detect Linux server roles based on running services and installed packages.
  /// </summary>
  private static IEnumerable<string> DetectLinuxServerRoles()
  {
    var roles = new List<string>();

    // Service-based detection using systemctl
    var serviceRoleMapping = new Dictionary<string, string[]>
    {
      // DNS
      { "named", new[] { "role:dns-server" } },
      { "bind9", new[] { "role:dns-server" } },
      { "dnsmasq", new[] { "role:dns-server", "role:dhcp-server" } },  // dnsmasq can do both
      { "unbound", new[] { "role:dns-server" } },
      
      // DHCP
      { "isc-dhcp-server", new[] { "role:dhcp-server" } },
      { "dhcpd", new[] { "role:dhcp-server" } },
      { "kea-dhcp4", new[] { "role:dhcp-server" } },
      { "kea-dhcp6", new[] { "role:dhcp-server" } },
      
      // Web Servers
      { "nginx", new[] { "role:web-server" } },
      { "apache2", new[] { "role:web-server" } },
      { "httpd", new[] { "role:web-server" } },
      { "caddy", new[] { "role:web-server" } },
      { "lighttpd", new[] { "role:web-server" } },
      
      // Database Servers
      { "mysql", new[] { "role:database-server" } },
      { "mysqld", new[] { "role:database-server" } },
      { "mariadb", new[] { "role:database-server" } },
      { "postgresql", new[] { "role:database-server" } },
      { "mongod", new[] { "role:database-server" } },
      { "redis-server", new[] { "role:cache-server" } },
      { "redis", new[] { "role:cache-server" } },
      { "memcached", new[] { "role:cache-server" } },
      
      // Mail Servers
      { "postfix", new[] { "role:mail-server" } },
      { "sendmail", new[] { "role:mail-server" } },
      { "exim4", new[] { "role:mail-server" } },
      { "dovecot", new[] { "role:mail-server" } },
      
      // File Sharing
      { "smbd", new[] { "role:file-server" } },
      { "nmbd", new[] { "role:file-server" } },
      { "nfs-server", new[] { "role:nfs-server" } },
      { "nfs-kernel-server", new[] { "role:nfs-server" } },
      { "vsftpd", new[] { "role:ftp-server" } },
      { "proftpd", new[] { "role:ftp-server" } },
      
      // Directory Services
      { "slapd", new[] { "role:ldap-server" } },
      { "sssd", new[] { "role:directory-client" } },
      { "winbind", new[] { "role:ad-member" } },
      { "samba-ad-dc", new[] { "role:domain-controller" } },
      
      // Virtualization & Containers
      { "docker", new[] { "role:container-host" } },
      { "containerd", new[] { "role:container-host" } },
      { "podman", new[] { "role:container-host" } },
      { "libvirtd", new[] { "role:hypervisor" } },
      { "qemu-kvm", new[] { "role:hypervisor" } },
      { "kubelet", new[] { "role:kubernetes-node" } },
      
      // Monitoring & Logging
      { "prometheus", new[] { "role:monitoring-server" } },
      { "grafana-server", new[] { "role:monitoring-server" } },
      { "zabbix-server", new[] { "role:monitoring-server" } },
      { "nagios", new[] { "role:monitoring-server" } },
      { "elasticsearch", new[] { "role:log-server" } },
      { "logstash", new[] { "role:log-server" } },
      { "rsyslog", new[] { "role:syslog-server" } },
      
      // Proxy & Load Balancing
      { "haproxy", new[] { "role:load-balancer" } },
      { "squid", new[] { "role:proxy-server" } },
      { "traefik", new[] { "role:reverse-proxy" } },
      
      // VPN
      { "openvpn", new[] { "role:vpn-server" } },
      { "wireguard", new[] { "role:vpn-server" } },
      { "strongswan", new[] { "role:vpn-server" } },
      
      // CI/CD
      { "jenkins", new[] { "role:ci-server" } },
      { "gitlab-runner", new[] { "role:ci-runner" } },
      
      // Message Queues
      { "rabbitmq-server", new[] { "role:message-queue" } },
      { "kafka", new[] { "role:message-queue" } },
      
      // SSH (indicates remote access capability)
      { "sshd", new[] { "role:remote-management" } },
      { "ssh", new[] { "role:remote-management" } },
    };

    try
    {
      // Get list of active services
      var psi = new ProcessStartInfo
      {
        FileName = "systemctl",
        Arguments = "list-units --type=service --state=running --no-legend --no-pager",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };

      using var process = Process.Start(psi);
      if (process != null)
      {
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);

        if (!string.IsNullOrEmpty(output))
        {
          var runningServices = output
              .Split('\n', StringSplitOptions.RemoveEmptyEntries)
              .Select(line =>
              {
                // Parse systemctl output: "service.service loaded active running description"
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 0 ? parts[0].Replace(".service", "") : "";
              })
              .Where(s => !string.IsNullOrEmpty(s))
              .ToHashSet(StringComparer.OrdinalIgnoreCase);

          foreach (var (service, roleArray) in serviceRoleMapping)
          {
            if (runningServices.Contains(service))
            {
              foreach (var role in roleArray)
              {
                if (!roles.Contains(role))
                {
                  roles.Add(role);
                }
              }
            }
          }
        }
      }
    }
    catch { }

    // Check for Kubernetes control plane
    try
    {
      if (File.Exists("/etc/kubernetes/admin.conf") ||
          Directory.Exists("/etc/kubernetes/manifests"))
      {
        roles.Add("role:kubernetes-control-plane");
      }
    }
    catch { }

    // Check if running in a container
    try
    {
      if (File.Exists("/.dockerenv") ||
          (File.Exists("/proc/1/cgroup") &&
           File.ReadAllText("/proc/1/cgroup").Contains("docker")))
      {
        roles.Add("env:container");
      }
    }
    catch { }

    // Check for cloud VM indicators
    try
    {
      var dmidecodeOutput = "";
      var psi = new ProcessStartInfo
      {
        FileName = "dmidecode",
        Arguments = "-s system-product-name",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };

      using var process = Process.Start(psi);
      if (process != null)
      {
        dmidecodeOutput = process.StandardOutput.ReadToEnd().Trim().ToLowerInvariant();
        process.WaitForExit(3000);

        if (dmidecodeOutput.Contains("virtual machine") || dmidecodeOutput.Contains("vmware"))
          roles.Add("env:vmware-vm");
        else if (dmidecodeOutput.Contains("kvm") || dmidecodeOutput.Contains("qemu"))
          roles.Add("env:kvm-vm");
        else if (dmidecodeOutput.Contains("hyper-v"))
          roles.Add("env:hyperv-vm");
        else if (dmidecodeOutput.Contains("xen"))
          roles.Add("env:xen-vm");
      }
    }
    catch { }

    return roles.Distinct();
  }

  /// <summary>
  /// Run a PowerShell command and return the output.
  /// </summary>
  private static string RunPowerShellCommand(string command)
  {
    try
    {
      var psi = new ProcessStartInfo
      {
        FileName = "powershell",
        Arguments = $"-NoProfile -NonInteractive -Command \"{command}\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };

      using var process = Process.Start(psi);
      if (process != null)
      {
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10000); // 10 second timeout
        return process.ExitCode == 0 ? output.Trim() : "";
      }
    }
    catch { }

    return "";
  }

  private static bool IsCommandAvailable(string command)
  {
    try
    {
      var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
      var psi = new System.Diagnostics.ProcessStartInfo
      {
        FileName = isWindows ? "where" : "which",
        Arguments = command,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };

      using var process = System.Diagnostics.Process.Start(psi);
      process?.WaitForExit(1000);
      return process?.ExitCode == 0;
    }
    catch
    {
      return false;
    }
  }

  private static string[] GetWorkspaceRoots()
  {
    // Check environment variable first
    var envRoots = Environment.GetEnvironmentVariable("FUNNEL_WORKSPACE_ROOTS");
    if (!string.IsNullOrEmpty(envRoots))
    {
      return envRoots.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
    }

    // Default to common development directories
    var roots = new List<string>();

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
      // Windows common paths
      var cCode = @"C:\Code";
      var userCode = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Code");
      var userProjects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects");

      if (Directory.Exists(cCode)) roots.Add(cCode);
      if (Directory.Exists(userCode)) roots.Add(userCode);
      if (Directory.Exists(userProjects)) roots.Add(userProjects);
    }
    else
    {
      // Unix common paths
      var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
      var homeCode = Path.Combine(home, "code");
      var homeProjects = Path.Combine(home, "projects");
      var homeDev = Path.Combine(home, "dev");

      if (Directory.Exists(homeCode)) roots.Add(homeCode);
      if (Directory.Exists(homeProjects)) roots.Add(homeProjects);
      if (Directory.Exists(homeDev)) roots.Add(homeDev);
    }

    // Fallback to home directory
    if (roots.Count == 0)
    {
      roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    return roots.ToArray();
  }

  private static string GetCertificateFingerprint()
  {
    // TODO: Load actual certificate and compute SHA256 fingerprint
    // For now, return placeholder
    var certPath = Environment.GetEnvironmentVariable("FUNNEL_CERT_PATH");
    if (!string.IsNullOrEmpty(certPath) && File.Exists(certPath))
    {
      try
      {
        using var cert = new X509Certificate2(certPath);
        return $"SHA256:{cert.GetCertHashString()}";
      }
      catch
      {
        // Fall through to placeholder
      }
    }

    return "SHA256:PLACEHOLDER_GENERATE_CERT";
  }

  /// <summary>
  /// Kill any existing FunnelCloud.Agent processes to free up ports.
  /// This allows re-running the agent without manually killing old instances.
  /// </summary>
  private static void KillExistingInstances()
  {
    var currentPid = Environment.ProcessId;
    var processName = "FunnelCloud.Agent";

    try
    {
      var existingProcesses = Process.GetProcessesByName(processName)
          .Where(p => p.Id != currentPid)
          .ToList();

      if (existingProcesses.Count > 0)
      {
        Console.WriteLine($"Found {existingProcesses.Count} existing FunnelCloud.Agent instance(s), terminating...");

        foreach (var proc in existingProcesses)
        {
          try
          {
            Console.WriteLine($"  Killing PID {proc.Id}...");
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(3000); // Wait up to 3 seconds
            Console.WriteLine($"  PID {proc.Id} terminated.");
          }
          catch (Exception ex)
          {
            Console.WriteLine($"  Warning: Could not kill PID {proc.Id}: {ex.Message}");
          }
          finally
          {
            proc.Dispose();
          }
        }

        // Brief pause to let ports release
        Thread.Sleep(500);
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"Warning: Error checking for existing instances: {ex.Message}");
    }
  }
}

