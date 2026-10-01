using System;
using System.Linq;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Widgets;

/// <summary>Connects the store to the profile lifecycle and the socket hub, so every write path (PUT, file import, cloud restore) broadcasts the same way.</summary>
public static class AppDataStoreWiring
{
    public static AppDataStore Create(ProfileManager profiles, MultiplexHub hub) => Wire(new AppDataStore(() => profiles.ActiveProfileId), profiles, hub);

    internal static AppDataStore Wire(AppDataStore store, ProfileManager profiles, MultiplexHub hub)
    {
        store.DocumentChanged += (profileId, appId, key) =>
        {
            // Topics are per (app, key); subscribers are viewing the active profile.
            if (!string.Equals(profileId, store.ActiveProfileId, StringComparison.Ordinal))
            {
                return;
            }
            var (revision, updatedAt, data) = store.Get(profileId, appId, key);
            AppDataTopics.Broadcast(hub, appId, key,
                new AppDataDocumentDto { ProfileId = profileId, Revision = revision, UpdatedAt = updatedAt, Data = data });
        };
        store.ResetRequested += profileId => AppDataTopics.BroadcastReset(hub, profileId);
        profiles.ProfileDeleted += store.DeleteProfile;
        profiles.ActiveProfileChanged += store.NotifyReset;
        profiles.LibraryReplaced += () => store.ArchiveProfilesExcept(profiles.GetManifest().Profiles.Select(p => p.Id).ToList());
        return store;
    }
}
