using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrchardCore.Environment.Shell.Builders;
using OrchardCore.Environment.Shell.Models;
using OrchardCore.Modules;

namespace OrchardCore.Environment.Shell.Distributed
{
    /// <summary>
    /// Keep in sync tenants by sharing shell identifiers through an <see cref="IDistributedCache"/>.
    /// </summary>
    internal class DistributedShellHostedService : BackgroundService
    {
        private const string ShellChangedIdKey = "SHELL_CHANGED_ID";
        private const string ShellCreatedIdKey = "SHELL_CREATED_ID";
        private const string ReleaseIdKeySuffix = "_RELEASE_ID";
        private const string ReloadIdKeySuffix = "_RELOAD_ID";

        private static readonly TimeSpan MinIdleTime = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaxBusyTime = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MaxRetryTime = TimeSpan.FromMinutes(1);

        private readonly IShellHost _shellHost;
        private readonly IShellContextFactory _shellContextFactory;
        private readonly IShellSettingsManager _shellSettingsManager;
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<string, ShellIdentifier> _identifiers = new ConcurrentDictionary<string, ShellIdentifier>();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new ConcurrentDictionary<string, SemaphoreSlim>();

        private string _shellChangedId;
        private string _shellCreatedId;

        private ShellContext _defaultContext;
        private DistributedContext _context;
        private bool _terminated;

        public DistributedShellHostedService(
            IShellHost shellHost,
            IShellContextFactory shellContextFactory,
            IShellSettingsManager shellSettingsManager,
            ILogger<DistributedShellHostedService> logger)
        {
            _shellHost = shellHost;
            _shellContextFactory = shellContextFactory;
            _shellSettingsManager = shellSettingsManager;
            _logger = logger;

            shellHost.LoadingAsync += LoadingAsync;
            shellHost.ReleasingAsync += ReleasingAsync;
            shellHost.ReloadingAsync += ReloadingAsync;
        }

        /// <summary>
        /// Keep in sync tenants by sharing shell identifiers through an <see cref="IDistributedCache"/>.
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            stoppingToken.Register(() =>
            {
                _logger.LogInformation("'{ServiceName}' is stopping.", nameof(DistributedShellHostedService));
            });

            try
            {
                var minIdleTime = MinIdleTime;
                while (!stoppingToken.IsCancellationRequested)
                {
                    // Waiting for the idle time on each loop.
                    if (!await TryWaitAsync(minIdleTime, stoppingToken))
                    {
                        break;
                    }

                    // If there is no default tenant or it is not running, nothing to do.
                    if (!_shellHost.TryGetShellContext(ShellHelper.DefaultShellName, out var defaultContext) ||
                        defaultContext.Settings.State != TenantState.Running)
                    {
                        continue;
                    }

                    // Check if the default tenant has changed.
                    if (_defaultContext != defaultContext)
                    {
                        _defaultContext = defaultContext;
                        var previousContext = _context;

                        // Create a new distributed context based on the default tenant settings.
                        _context = await CreateDistributedContextAsync(defaultContext.Settings);

                        // Release the previous one.
                        previousContext?.Release();
                    }

                    // If the required distributed features are not enabled, nothing to do.
                    var distributedCache = _context.DistributedCache;
                    if (distributedCache == null)
                    {
                        continue;
                    }

                    // Try to retrieve the tenant changed global identifier from the distributed cache.
                    string shellChangedId;
                    try
                    {
                        shellChangedId = await distributedCache.GetStringAsync(ShellChangedIdKey);
                    }
                    catch (Exception ex) when (!ex.IsFatal())
                    {
                        // We will retry but after a longer idle time.
                        if (minIdleTime < MaxRetryTime)
                        {
                            minIdleTime *= 2;
                            if (minIdleTime >= MaxRetryTime)
                            {
                                // Log the error only once.
                                _logger.LogError(ex, "Unable to read the distributed cache before checking if a tenant has changed.");
                                minIdleTime = MaxRetryTime;
                            }
                        }

                        continue;
                    }

                    // Reset the min idle time if it was increased.
                    minIdleTime = MinIdleTime;

                    // Check if at least one tenant has changed.
                    if (shellChangedId == null || _shellChangedId == shellChangedId)
                    {
                        continue;
                    }

                    // Keep in sync the tenant changed global identifier.
                    _shellChangedId = shellChangedId;

                    // Try to retrieve the tenant created global identifier from the distributed cache.
                    string shellCreatedId;
                    try
                    {
                        shellCreatedId = await distributedCache.GetStringAsync(ShellCreatedIdKey);
                    }
                    catch (Exception ex) when (!ex.IsFatal())
                    {
                        _logger.LogError(ex, "Unable to read the distributed cache before checking if a tenant has been created.");
                        continue;
                    }

                    // Retrieve all tenant settings that are already loaded.
                    var allSettings = _shellHost.GetAllSettings().ToList();

                    // Check if at least one tenant has been created.
                    if (shellCreatedId != null && _shellCreatedId != shellCreatedId)
                    {
                        // Keep in sync the tenant created global identifier.
                        _shellCreatedId = shellCreatedId;

                        // Retrieve all new created tenants that are not already loaded.
                        var names = await _shellSettingsManager.LoadSettingsNamesAsync();
                        foreach (var name in names.Except(allSettings.Select(s => s.Name)))
                        {
                            // Load and enlist the settings of each new created tenant.
                            allSettings.Add(await _shellSettingsManager.LoadSettingsAsync(name));
                        }
                    }

                    // start of the busy period.
                    var startTime = DateTime.UtcNow;

                    // Keep in sync all tenants by checking their specific identifiers.
                    foreach (var settings in allSettings)
                    {
                        // If busy for a too long time, wait again.
                        if (DateTime.UtcNow - startTime > MaxBusyTime)
                        {
                            if (!await TryWaitAsync(MinIdleTime, stoppingToken))
                            {
                                break;
                            }

                            startTime = DateTime.UtcNow;
                        }

                        var semaphore = _semaphores.GetOrAdd(settings.Name, name => new SemaphoreSlim(1));
                        await semaphore.WaitAsync();
                        try
                        {
                            // Try to retrieve the release identifier of this tenant from the distributed cache.
                            var releaseId = await distributedCache.GetStringAsync(settings.Name + ReleaseIdKeySuffix);
                            if (releaseId != null)
                            {
                                // Check if the release identifier of this tenant has changed.
                                var identifier = _identifiers.GetOrAdd(settings.Name, name => new ShellIdentifier());
                                if (identifier.ReleaseId != releaseId)
                                {
                                    // Upate the local identifier.
                                    identifier.ReleaseId = releaseId;

                                    // Keep in sync this tenant by releasing it locally.
                                    await _shellHost.ReleaseShellContextAsync(settings, eventSource: false);
                                }
                            }

                            // Try to retrieve the reload identifier of this tenant from the distributed cache.
                            var reloadId = await distributedCache.GetStringAsync(settings.Name + ReloadIdKeySuffix);
                            if (reloadId != null)
                            {
                                // Check if the reload identifier of this tenant has changed.
                                var identifier = _identifiers.GetOrAdd(settings.Name, name => new ShellIdentifier());
                                if (identifier.ReloadId != reloadId)
                                {
                                    // Upate the local identifier.
                                    identifier.ReloadId = reloadId;

                                    // Keep in sync this tenant by reloading it locally.
                                    await _shellHost.ReloadShellContextAsync(settings, eventSource: false);
                                }
                            }
                        }
                        catch (Exception ex) when (!ex.IsFatal())
                        {
                            _logger.LogError(ex, "Unable to read the distributed cache while syncing the tenant '{TenantName}'.", settings.Name);
                            break;
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }
                }
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                _logger.LogError(ex, "Error while executing '{ServiceName}', the service is stopping.", nameof(DistributedShellHostedService));
            }

            _terminated = true;
            _context?.Release();
            _defaultContext = null;
            _context = null;
        }

        /// <summary>
        /// Called before loading all tenants to initialize the local shell identifiers from the distributed cache.
        /// </summary>
        public async Task LoadingAsync()
        {
            if (_terminated)
            {
                return;
            }

            // If there is no default tenant or it is not running, nothing to do.
            var defautSettings = await _shellSettingsManager.LoadSettingsAsync(ShellHelper.DefaultShellName);
            if (defautSettings?.State != TenantState.Running)
            {
                return;
            }

            // Create a distributed context as the shared one is not yet built.
            using var context = await CreateDistributedContextAsync(defautSettings);

            // If the required distributed features are not enabled, nothing to do.
            var distributedCache = context.DistributedCache;
            if (distributedCache == null)
            {
                return;
            }

            try
            {
                // Retrieve the tenant global identifiers from the distributed cache.
                _shellChangedId = await distributedCache.GetStringAsync(ShellChangedIdKey);
                _shellCreatedId = await distributedCache.GetStringAsync(ShellCreatedIdKey);

                // Retrieve the names of all the tenants.
                var names = await _shellSettingsManager.LoadSettingsNamesAsync();
                foreach (var name in names)
                {
                    // Retrieve the release identifier of this tenant from the distributed cache.
                    var releaseId = await distributedCache.GetStringAsync(name + ReleaseIdKeySuffix);
                    if (releaseId != null)
                    {
                        // Initialize the release identifier of this tenant in the local collection.
                        var identifier = _identifiers.GetOrAdd(name, name => new ShellIdentifier());
                        identifier.ReleaseId = releaseId;
                    }

                    // Retrieve the reload identifier of this tenant from the distributed cache.
                    var reloadId = await distributedCache.GetStringAsync(name + ReloadIdKeySuffix);
                    if (reloadId != null)
                    {
                        // Initialize the reload identifier of this tenant in the local collection.
                        var identifier = _identifiers.GetOrAdd(name, name => new ShellIdentifier());
                        identifier.ReloadId = reloadId;
                    }
                }
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                _logger.LogError(ex, "Unable to read the distributed cache before loading all tenants.");
            }
        }

        /// <summary>
        /// Called before releasing a tenant to update the related shell identifiers, locally and in the distributed cache.
        /// </summary>
        public async Task ReleasingAsync(string name)
        {
            if (_terminated)
            {
                return;
            }

            // If there is no default tenant or it is not running, nothing to do.
            if (!_shellHost.TryGetSettings(ShellHelper.DefaultShellName, out var settings) ||
                settings.State != TenantState.Running)
            {
                return;
            }

            // Acquire the distributed context or create a new one if not yet built.
            using var context = await AcquireOrCreateDistributedContextAsync(settings);

            // If the required distributed features are not enabled, nothing to do.
            var distributedCache = context.DistributedCache;
            if (distributedCache == null)
            {
                return;
            }

            var semaphore = _semaphores.GetOrAdd(name, name => new SemaphoreSlim(1));
            await semaphore.WaitAsync();
            try
            {
                // Update this tenant in the local collection with a new release identifier.
                var identifier = _identifiers.GetOrAdd(name, name => new ShellIdentifier());
                identifier.ReleaseId = IdGenerator.GenerateId();

                // Update the release identifier of this tenant in the distributed cache.
                await distributedCache.SetStringAsync(name + ReleaseIdKeySuffix, identifier.ReleaseId);

                // Also update the global identifier specifying that a tenant has changed.
                await distributedCache.SetStringAsync(ShellChangedIdKey, identifier.ReleaseId);
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                _logger.LogError(ex, "Unable to update the distributed cache before releasing the tenant '{TenantName}'.", name);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Called before reloading a tenant to update the related shell identifiers, locally and in the distributed cache.
        /// </summary>
        public async Task ReloadingAsync(string name)
        {
            if (_terminated)
            {
                return;
            }

            // If there is no default tenant or it is not running, nothing to do.
            if (!_shellHost.TryGetSettings(ShellHelper.DefaultShellName, out var settings) ||
                settings.State != TenantState.Running)
            {
                return;
            }

            // Acquire the distributed context or create a new one if not yet built.
            using var context = await AcquireOrCreateDistributedContextAsync(settings);

            // If the required distributed features are not enabled, nothing to do.
            var distributedCache = context.DistributedCache;
            if (distributedCache == null)
            {
                return;
            }

            var semaphore = _semaphores.GetOrAdd(name, name => new SemaphoreSlim(1));
            await semaphore.WaitAsync();
            try
            {
                // Update this tenant in the local collection with a new reload identifier.
                var identifier = _identifiers.GetOrAdd(name, name => new ShellIdentifier());
                identifier.ReloadId = IdGenerator.GenerateId();

                // Update the reload identifier of this tenant in the distributed cache.
                await distributedCache.SetStringAsync(name + ReloadIdKeySuffix, identifier.ReloadId);

                // Check if it is a new created tenant that has not been already loaded.
                if (name != ShellHelper.DefaultShellName && !_shellHost.TryGetSettings(name, out _))
                {
                    // Also update the global identifier specifying that a tenant has been created.
                    await distributedCache.SetStringAsync(ShellCreatedIdKey, identifier.ReloadId);
                }

                // Also update the global identifier specifying that a tenant has changed.
                await distributedCache.SetStringAsync(ShellChangedIdKey, identifier.ReloadId);
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                _logger.LogError(ex, "Unable to update the distributed cache before reloading the tenant '{TenantName}'.", name);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Creates a distributed context to be shared and based on the provided shell settings.
        /// </summary>
        private async Task<DistributedContext> CreateDistributedContextAsync(ShellSettings settings) =>
            new DistributedContext(await _shellContextFactory.CreateShellContextAsync(settings));

        /// <summary>
        /// Acquires the shared distributed context or creates a new one if not yet initialized.
        /// </summary>
        private Task<DistributedContext> AcquireOrCreateDistributedContextAsync(ShellSettings settings)
        {
            var distributedContext = _context?.Acquire();
            if (distributedContext == null)
            {
                return CreateDistributedContextAsync(settings);
            }

            return Task.FromResult(distributedContext);
        }

        /// <summary>
        /// Tries to wait for a given delay, returns false if it has been cancelled.
        /// </summary>
        private async Task<bool> TryWaitAsync(TimeSpan delay, CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(delay, stoppingToken);
                return true;
            }
            catch (TaskCanceledException)
            {
                return false;
            }
        }
    }
}
