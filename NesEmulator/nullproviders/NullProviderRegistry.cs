using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NesEmulator.NullProviders;

/// <summary>
/// Registry for discovering and instantiating null providers via reflection.
/// Automatically finds all types implementing INullProvider in the current assembly.
/// </summary>
public static class NullProviderRegistry
{
    private static readonly Lazy<List<Type>> _providerTypes = new Lazy<List<Type>>(DiscoverProviders);
    private static readonly Dictionary<string, INullProvider> _providerCache = new Dictionary<string, INullProvider>();
    
    /// <summary>
    /// Get all available null provider display names
    /// </summary>
    public static IEnumerable<string> GetAvailableProviders()
    {
        return _providerTypes.Value
            .Select(t => GetProviderInstance(t).DisplayName)
            .OrderBy(name => name);
    }
    
    /// <summary>
    /// Create or retrieve a null provider instance by display name
    /// </summary>
    /// <param name="displayName">The display name of the provider to retrieve</param>
    /// <returns>An instance of the requested provider, or StaticNullProvider if not found</returns>
    public static INullProvider GetProvider(string displayName)
    {
        // Check cache first
        if (_providerCache.TryGetValue(displayName, out var cached))
        {
            return cached;
        }
        
        // Find provider type by display name
        var providerType = _providerTypes.Value.FirstOrDefault(t =>
        {
            var instance = GetProviderInstance(t);
            return instance.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase);
        });
        
        if (providerType == null)
        {
            // Fallback to static provider if requested provider not found
            // Avoid infinite recursion by checking if we're already looking for the default
            if (!displayName.Equals("Static", StringComparison.OrdinalIgnoreCase))
            {
                return GetDefaultProvider();
            }
            
            // Ultimate fallback: find StaticNullProvider by type if display name lookup fails
            var staticType = _providerTypes.Value.FirstOrDefault(t => 
                t.Name.Equals("StaticNullProvider", StringComparison.OrdinalIgnoreCase));
            
            if (staticType != null)
            {
                var provider = GetProviderInstance(staticType);
                _providerCache[displayName] = provider;
                return provider;
            }
            
            // If all else fails, return the first available provider
            if (_providerTypes.Value.Count > 0)
            {
                var fallbackProvider = GetProviderInstance(_providerTypes.Value[0]);
                _providerCache[displayName] = fallbackProvider;
                return fallbackProvider;
            }
            
            throw new InvalidOperationException("No null providers available in the registry");
        }
        
        var finalProvider = GetProviderInstance(providerType);
        _providerCache[displayName] = finalProvider;
        return finalProvider;
    }
    
    /// <summary>
    /// Get the default null provider (Static)
    /// </summary>
    public static INullProvider GetDefaultProvider()
    {
        return GetProvider("Static");
    }
    
    /// <summary>
    /// Discover all types implementing INullProvider in the current assembly
    /// </summary>
    private static List<Type> DiscoverProviders()
    {
        var interfaceType = typeof(INullProvider);
        var assembly = Assembly.GetExecutingAssembly();
        
        return assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && interfaceType.IsAssignableFrom(t))
            .ToList();
    }
    
    /// <summary>
    /// Create an instance of a provider type (or retrieve from cache)
    /// </summary>
    private static INullProvider GetProviderInstance(Type providerType)
    {
        var name = providerType.FullName ?? providerType.Name;
        if (_providerCache.TryGetValue(name, out var cached))
        {
            return cached;
        }
        
        var instance = (INullProvider)Activator.CreateInstance(providerType)!;
        _providerCache[name] = instance;
        return instance;
    }
}
