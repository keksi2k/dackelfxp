using DackelFxp.Ftp;
using System.Security.Cryptography;
using DackelFxp.Core.Infrastructure;

namespace DackelFxp.Core.Sites;

/// <summary>Loads and saves the site list; passwords are encrypted on disk.</summary>
public sealed class SiteStore
{
    private readonly AppPaths _paths;
    private readonly Lock _lock = new();
    private SecretProtector? _protector;
    private SitesFile _file = new();
    private readonly List<Site> _temporary = [];

    public SiteStore(AppPaths paths) => _paths = paths;

    public event Action? Changed;

    public IReadOnlyList<Site> Sites
    {
        get { lock (_lock) return _file.Sites.ToList(); }
    }

    public IReadOnlyList<string> Groups
    {
        get { lock (_lock) return _file.Groups.ToList(); }
    }

    public bool UsesMasterPassword => _file.MasterSalt is not null;
    public bool IsUnlocked => _protector is not null;

    /// <summary>Loads the file. If a master password is set, <see cref="Unlock"/> must be called afterwards.</summary>
    public void Load()
    {
        lock (_lock)
        {
            _file = Json.Load<SitesFile>(_paths.SitesFile) ?? new SitesFile();
            if (_file.MasterSalt is null)
            {
                _protector = SecretProtector.FromKeyFile(_paths.KeyFile);
                DecryptAll();
            }
        }
    }

    public bool Unlock(string masterPassword)
    {
        lock (_lock)
        {
            if (_file.MasterSalt is null) return true;
            var protector = SecretProtector.FromMasterPassword(masterPassword, Convert.FromBase64String(_file.MasterSalt));
            if (_file.MasterVerifier is null || !protector.Verify(_file.MasterVerifier)) return false;
            _protector = protector;
            DecryptAll();
            return true;
        }
    }

    /// <summary>Sets or (with null) removes the master password and re-encrypts all passwords.</summary>
    public void SetMasterPassword(string? masterPassword)
    {
        lock (_lock)
        {
            if (masterPassword is null)
            {
                _file.MasterSalt = null;
                _file.MasterVerifier = null;
                _protector = SecretProtector.FromKeyFile(_paths.KeyFile);
            }
            else
            {
                var salt = SecretProtector.NewSalt();
                _protector = SecretProtector.FromMasterPassword(masterPassword, salt);
                _file.MasterSalt = Convert.ToBase64String(salt);
                _file.MasterVerifier = _protector.CreateVerifier();
            }
            SaveCore();
        }
    }

    public Site? Find(Guid id)
    {
        lock (_lock) return _file.Sites.FirstOrDefault(s => s.Id == id) ?? _temporary.FirstOrDefault(s => s.Id == id);
    }

    /// <summary>Registers a quick-connect site for this session (not saved).</summary>
    public void AddTemporary(Site site)
    {
        site.IsTemporary = true;
        lock (_lock)
        {
            _temporary.RemoveAll(s => s.Id == site.Id);
            _temporary.Add(site);
        }
    }

    public void AddOrUpdate(Site site)
    {
        lock (_lock)
        {
            site.IsTemporary = false;
            _temporary.RemoveAll(s => s.Id == site.Id);
            var idx = _file.Sites.FindIndex(s => s.Id == site.Id);
            if (idx >= 0) _file.Sites[idx] = site;
            else _file.Sites.Add(site);
            if (site.Group.Length > 0 && !_file.Groups.Contains(site.Group)) _file.Groups.Add(site.Group);
            SaveCore();
        }
        Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        lock (_lock)
        {
            _file.Sites.RemoveAll(s => s.Id == id);
            SaveCore();
        }
        Changed?.Invoke();
    }

    public void AddGroup(string group)
    {
        lock (_lock)
        {
            if (!_file.Groups.Contains(group)) _file.Groups.Add(group);
            SaveCore();
        }
        Changed?.Invoke();
    }

    public void RemoveGroup(string group)
    {
        lock (_lock)
        {
            _file.Groups.RemoveAll(g => g == group || g.StartsWith(group + "/", StringComparison.Ordinal));
            foreach (var site in _file.Sites.Where(s => s.Group == group || s.Group.StartsWith(group + "/", StringComparison.Ordinal)))
                site.Group = "";
            SaveCore();
        }
        Changed?.Invoke();
    }

    public void Save()
    {
        lock (_lock) SaveCore();
        Changed?.Invoke();
    }

    private void DecryptAll()
    {
        foreach (var site in _file.Sites)
        {
            try { site.Password = _protector!.Unprotect(site.ProtectedPassword); }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { site.Password = ""; }
        }
    }

    private void SaveCore()
    {
        if (_protector is null) throw new InvalidOperationException(Lang.T("Site-Liste ist gesperrt (Master-Passwort)", "Site list is locked (master password)"));
        foreach (var site in _file.Sites)
            site.ProtectedPassword = _protector.Protect(site.Password);
        var persisted = new SitesFile
        {
            MasterSalt = _file.MasterSalt,
            MasterVerifier = _file.MasterVerifier,
            Groups = _file.Groups,
            Sites = _file.Sites.Where(s => !s.IsTemporary).ToList(),
        };
        Json.Save(_paths.SitesFile, persisted);
        AppPaths.RestrictToUser(_paths.SitesFile);
    }

    private sealed class SitesFile
    {
        public string? MasterSalt { get; set; }
        public string? MasterVerifier { get; set; }
        public List<string> Groups { get; set; } = [];
        public List<Site> Sites { get; set; } = [];
    }
}
