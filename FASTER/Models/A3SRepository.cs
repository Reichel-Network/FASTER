using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Xml.Serialization;

namespace FASTER.Models
{
    /// <summary>
    /// FASTER-owned A3S repository list persisted in user settings (source of truth).
    /// Classic remote <c>.a3s/*</c> metadata is read/written later via <c>IA3SMetadataStore</c>.
    /// </summary>
    [Serializable]
    public class A3SRepositoryCollection : INotifyPropertyChanged
    {
        private ObservableCollection<A3SRepository> _repositories = new();

        [XmlElement(Order = 1, ElementName = "A3SRepository")]
        public ObservableCollection<A3SRepository> Repositories
        {
            get => _repositories;
            set
            {
                _repositories = value ?? new ObservableCollection<A3SRepository>();
                RaisePropertyChanged(nameof(Repositories));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void RaisePropertyChanged(string property)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    /// <summary>
    /// A community or publish A3S repository tracked by FASTER.
    /// </summary>
    [Serializable]
    public class A3SRepository : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString("N");
        private string _name = string.Empty;
        private string _autoconfigUrl = string.Empty;
        private string _localPath = string.Empty;
        private long _lastRevision;
        private DateTime? _lastCheckUtc;
        private string _protocolHint = string.Empty;
        private string _publishRoot = string.Empty;
        private bool _useDeploymentInstallPath;
        private string _uploadProtocol = "HTTPS";
        private string _uploadUrl = string.Empty;
        private string _uploadPort = string.Empty;
        private string _uploadLogin = string.Empty;
        private string _uploadPassword = string.Empty;
        private int _numberOfConnections = 4;
        private DateTime? _lastBuildUtc;
        private DateTime? _lastPublishUtc;

        public A3SRepository()
        {
        }

        public A3SRepository(string name, string autoconfigUrl)
        {
            Name = name;
            AutoconfigUrl = autoconfigUrl;
        }

        [XmlElement(Order = 1)]
        public string Id
        {
            get => _id;
            set
            {
                _id = string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value;
                RaisePropertyChanged(nameof(Id));
            }
        }

        [XmlElement(Order = 2)]
        public string Name
        {
            get => _name;
            set
            {
                _name = value ?? string.Empty;
                RaisePropertyChanged(nameof(Name));
            }
        }

        /// <summary>
        /// Autoconfig URL of the form <c>{scheme}://{host}[:port]{remotePath}/.a3s/autoconfig</c>.
        /// </summary>
        [XmlElement(Order = 3)]
        public string AutoconfigUrl
        {
            get => _autoconfigUrl;
            set
            {
                _autoconfigUrl = value ?? string.Empty;
                RaisePropertyChanged(nameof(AutoconfigUrl));
            }
        }

        [XmlElement(Order = 4)]
        public string LocalPath
        {
            get => _localPath;
            set
            {
                _localPath = value ?? string.Empty;
                RaisePropertyChanged(nameof(LocalPath));
            }
        }

        [XmlElement(Order = 5)]
        public long LastRevision
        {
            get => _lastRevision;
            set
            {
                _lastRevision = value;
                RaisePropertyChanged(nameof(LastRevision));
            }
        }

        [XmlElement(Order = 6)]
        public DateTime? LastCheckUtc
        {
            get => _lastCheckUtc;
            set
            {
                _lastCheckUtc = value;
                RaisePropertyChanged(nameof(LastCheckUtc));
            }
        }

        /// <summary>
        /// Optional protocol hint (HTTP, FTP, SFTP, WebDAV) derived from autoconfig later.
        /// </summary>
        [XmlElement(Order = 7)]
        public string ProtocolHint
        {
            get => _protocolHint;
            set
            {
                _protocolHint = value ?? string.Empty;
                RaisePropertyChanged(nameof(ProtocolHint));
            }
        }

        /// <summary>
        /// Dedicated publish content root. When empty, <see cref="LocalPath"/> is used
        /// (or Deployment InstallPath when <see cref="UseDeploymentInstallPath"/> is set).
        /// </summary>
        [XmlElement(Order = 8)]
        public string PublishRoot
        {
            get => _publishRoot;
            set
            {
                _publishRoot = value ?? string.Empty;
                RaisePropertyChanged(nameof(PublishRoot));
            }
        }

        /// <summary>When true, resolve publish root from Settings.Deployments.InstallPath.</summary>
        [XmlElement(Order = 9)]
        public bool UseDeploymentInstallPath
        {
            get => _useDeploymentInstallPath;
            set
            {
                _useDeploymentInstallPath = value;
                RaisePropertyChanged(nameof(UseDeploymentInstallPath));
            }
        }

        /// <summary>Upload protocol: HTTP, HTTPS, or SFTP (C# upload; not Arma3Sync GUI).</summary>
        [XmlElement(Order = 10)]
        public string UploadProtocol
        {
            get => _uploadProtocol;
            set
            {
                _uploadProtocol = string.IsNullOrWhiteSpace(value) ? "HTTPS" : value.Trim();
                RaisePropertyChanged(nameof(UploadProtocol));
            }
        }

        /// <summary>
        /// Upload host + optional remote path (Arma3Sync style, no scheme), e.g. <c>host.example/repo</c>.
        /// Empty HTTP(S) upload falls back to the autoconfig content root.
        /// </summary>
        [XmlElement(Order = 11)]
        public string UploadUrl
        {
            get => _uploadUrl;
            set
            {
                _uploadUrl = value ?? string.Empty;
                RaisePropertyChanged(nameof(UploadUrl));
            }
        }

        [XmlElement(Order = 12)]
        public string UploadPort
        {
            get => _uploadPort;
            set
            {
                _uploadPort = value ?? string.Empty;
                RaisePropertyChanged(nameof(UploadPort));
            }
        }

        [XmlElement(Order = 13)]
        public string UploadLogin
        {
            get => _uploadLogin;
            set
            {
                _uploadLogin = value ?? string.Empty;
                RaisePropertyChanged(nameof(UploadLogin));
            }
        }

        /// <summary>Upload password stored in user settings (local machine scope).</summary>
        [XmlElement(Order = 14)]
        public string UploadPassword
        {
            get => _uploadPassword;
            set
            {
                _uploadPassword = value ?? string.Empty;
                RaisePropertyChanged(nameof(UploadPassword));
            }
        }

        [XmlElement(Order = 15)]
        public int NumberOfConnections
        {
            get => _numberOfConnections;
            set
            {
                _numberOfConnections = value <= 0 ? 1 : value;
                RaisePropertyChanged(nameof(NumberOfConnections));
            }
        }

        [XmlElement(Order = 16)]
        public DateTime? LastBuildUtc
        {
            get => _lastBuildUtc;
            set
            {
                _lastBuildUtc = value;
                RaisePropertyChanged(nameof(LastBuildUtc));
            }
        }

        [XmlElement(Order = 17)]
        public DateTime? LastPublishUtc
        {
            get => _lastPublishUtc;
            set
            {
                _lastPublishUtc = value;
                RaisePropertyChanged(nameof(LastPublishUtc));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void RaisePropertyChanged(string property)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
