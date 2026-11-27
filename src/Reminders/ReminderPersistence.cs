using System.Collections.Generic;
using Nexus.Service.Models.Reminders;

namespace Nexus.Service.Reminders;

/// <summary><c>&lt;NexusRoot&gt;/reminders/reminders.json</c>. Local data owned by the service, deliberately not part of NexusSettings.</summary>
public sealed class ReminderStoreFile
{
    public List<ReminderListDto> Lists { get; set; } = new();
    public List<ReminderDto> Items { get; set; } = new();
}
