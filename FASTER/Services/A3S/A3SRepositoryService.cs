using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using FASTER.Models;
using FASTER.Properties;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Facade for listing, adding, removing, and persisting A3S repositories in FASTER settings.
    /// Source of truth is <see cref="Settings.a3sRepositories"/> — not %APPDATA%\Arma3Sync.
    /// </summary>
    public class A3SRepositoryService
    {
        private readonly Settings _settings;

        public A3SRepositoryService()
            : this(Settings.Default)
        {
        }

        internal A3SRepositoryService(Settings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            EnsureCollection();
        }

        /// <summary>Repositories currently persisted in user settings.</summary>
        public ObservableCollection<A3SRepository> Repositories
        {
            get
            {
                EnsureCollection();
                return _settings.a3sRepositories.Repositories;
            }
        }

        /// <summary>Snapshot list of repositories.</summary>
        public IReadOnlyList<A3SRepository> List()
        {
            EnsureCollection();
            return _settings.a3sRepositories.Repositories.ToList();
        }

        /// <summary>
        /// Create a repository from name + autoconfig URL without contacting the network (Phase 0).
        /// </summary>
        public A3SRepository ImportByAutoconfigUrl(string name, string autoconfigUrl, string? localPath = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Repository name is required.", nameof(name));
            if (string.IsNullOrWhiteSpace(autoconfigUrl))
                throw new ArgumentException("Autoconfig URL is required.", nameof(autoconfigUrl));

            EnsureCollection();

            var repo = new A3SRepository(name.Trim(), autoconfigUrl.Trim())
            {
                LocalPath = string.IsNullOrWhiteSpace(localPath)
                    ? DeriveDefaultLocalPath(name.Trim())
                    : localPath.Trim()
            };

            _settings.a3sRepositories.Repositories.Add(repo);
            Persist();
            return repo;
        }

        /// <summary>Add an existing repository instance and persist.</summary>
        public void Add(A3SRepository repository)
        {
            ArgumentNullException.ThrowIfNull(repository);
            if (string.IsNullOrWhiteSpace(repository.Name))
                throw new ArgumentException("Repository name is required.", nameof(repository));
            if (string.IsNullOrWhiteSpace(repository.AutoconfigUrl))
                throw new ArgumentException("Autoconfig URL is required.", nameof(repository));

            EnsureCollection();
            _settings.a3sRepositories.Repositories.Add(repository);
            Persist();
        }

        /// <summary>Remove by id and persist. Returns true if a repository was removed.</summary>
        public bool Remove(string id)
        {
            EnsureCollection();
            var existing = _settings.a3sRepositories.Repositories.FirstOrDefault(r => r.Id == id);
            if (existing == null)
                return false;

            _settings.a3sRepositories.Repositories.Remove(existing);
            Persist();
            return true;
        }

        /// <summary>Remove the given instance and persist.</summary>
        public bool Remove(A3SRepository repository)
        {
            ArgumentNullException.ThrowIfNull(repository);
            return Remove(repository.Id);
        }

        /// <summary>
        /// Update publish/upload configuration for an existing repository and persist.
        /// FASTER settings remain source of truth (no sealed <c>*.a3s.repository</c> required).
        /// </summary>
        public A3SRepository ConfigurePublish(
            A3SRepository repository,
            string? publishRoot = null,
            bool? useDeploymentInstallPath = null,
            string? uploadProtocol = null,
            string? uploadUrl = null,
            string? uploadPort = null,
            string? uploadLogin = null,
            string? uploadPassword = null,
            int? numberOfConnections = null,
            string? autoconfigUrl = null,
            string? localPath = null)
        {
            ArgumentNullException.ThrowIfNull(repository);
            EnsureCollection();

            var existing = _settings.a3sRepositories.Repositories.FirstOrDefault(r => r.Id == repository.Id)
                           ?? repository;

            if (publishRoot != null)
                existing.PublishRoot = publishRoot.Trim();
            if (useDeploymentInstallPath.HasValue)
                existing.UseDeploymentInstallPath = useDeploymentInstallPath.Value;
            if (uploadProtocol != null)
                existing.UploadProtocol = uploadProtocol.Trim();
            if (uploadUrl != null)
                existing.UploadUrl = uploadUrl.Trim();
            if (uploadPort != null)
                existing.UploadPort = uploadPort.Trim();
            if (uploadLogin != null)
                existing.UploadLogin = uploadLogin.Trim();
            if (uploadPassword != null)
                existing.UploadPassword = uploadPassword;
            if (numberOfConnections.HasValue)
                existing.NumberOfConnections = numberOfConnections.Value;
            if (autoconfigUrl != null)
                existing.AutoconfigUrl = autoconfigUrl.Trim();
            if (localPath != null)
                existing.LocalPath = localPath.Trim();

            Persist();
            return existing;
        }

        /// <summary>Apply Deployment InstallPath as this repository's publish root.</summary>
        public A3SRepository UseDeploymentInstallPathAsPublishRoot(A3SRepository repository)
        {
            ArgumentNullException.ThrowIfNull(repository);
            var install = _settings.Deployments?.InstallPath;
            if (string.IsNullOrWhiteSpace(install))
                throw new InvalidOperationException("Deployment InstallPath is not set.");

            return ConfigurePublish(
                repository,
                publishRoot: install,
                useDeploymentInstallPath: true,
                localPath: string.IsNullOrWhiteSpace(repository.LocalPath) ? install : null);
        }

        /// <summary>Force settings write of the current collection.</summary>
        public void Persist()
        {
            EnsureCollection();
            _settings.a3sRepositories = _settings.a3sRepositories;
            _settings.Save();
        }

        private void EnsureCollection()
        {
            if (_settings.a3sRepositories == null)
                _settings.a3sRepositories = new A3SRepositoryCollection();
        }

        private string DeriveDefaultLocalPath(string name)
        {
            var root = string.IsNullOrWhiteSpace(_settings.a3sDefaultSyncPath)
                ? _settings.modStagingDirectory
                : _settings.a3sDefaultSyncPath;

            if (string.IsNullOrWhiteSpace(root))
                return string.Empty;

            return System.IO.Path.Combine(root, Functions.SafeName(name));
        }
    }
}
