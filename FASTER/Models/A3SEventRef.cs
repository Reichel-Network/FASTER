using System;
using System.ComponentModel;
using System.Xml.Serialization;

namespace FASTER.Models
{
    /// <summary>
    /// Link between an A3S event (modset) and a FASTER server profile.
    /// </summary>
    [Serializable]
    public class A3SEventRef : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _profileId = string.Empty;
        private string _repositoryId = string.Empty;
        private string _description = string.Empty;

        public A3SEventRef()
        {
        }

        public A3SEventRef(string name, string profileId, string repositoryId = "")
        {
            Name = name;
            ProfileId = profileId;
            RepositoryId = repositoryId;
        }

        /// <summary>A3S event name (modset).</summary>
        [XmlElement(Order = 1)]
        public string Name
        {
            get => _name;
            set
            {
                _name = value ?? string.Empty;
                RaisePropertyChanged(nameof(Name));
            }
        }

        /// <summary>Target <see cref="ServerProfile.Id"/> for event ↔ profile mapping.</summary>
        [XmlElement(Order = 2)]
        public string ProfileId
        {
            get => _profileId;
            set
            {
                _profileId = value ?? string.Empty;
                RaisePropertyChanged(nameof(ProfileId));
            }
        }

        /// <summary>Source <see cref="A3SRepository.Id"/>.</summary>
        [XmlElement(Order = 3)]
        public string RepositoryId
        {
            get => _repositoryId;
            set
            {
                _repositoryId = value ?? string.Empty;
                RaisePropertyChanged(nameof(RepositoryId));
            }
        }

        [XmlElement(Order = 4)]
        public string Description
        {
            get => _description;
            set
            {
                _description = value ?? string.Empty;
                RaisePropertyChanged(nameof(Description));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void RaisePropertyChanged(string property)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
