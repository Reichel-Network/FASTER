using System;
using System.ComponentModel;
using System.Xml.Serialization;

namespace FASTER.Models
{
    /// <summary>
    /// Stub link between an A3S event (modset) and a FASTER server profile.
    /// Wired in Phase 3 — unused by Phase 0 UI.
    /// </summary>
    [Serializable]
    public class A3SEventRef : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _profileId = string.Empty;

        public A3SEventRef()
        {
        }

        public A3SEventRef(string name, string profileId)
        {
            Name = name;
            ProfileId = profileId;
        }

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

        /// <summary>
        /// Target <see cref="ServerProfile.Id"/> for event ↔ profile mapping (Phase 3).
        /// </summary>
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

        public event PropertyChangedEventHandler? PropertyChanged;

        private void RaisePropertyChanged(string property)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
