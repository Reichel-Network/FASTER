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

        public event PropertyChangedEventHandler? PropertyChanged;

        private void RaisePropertyChanged(string property)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
