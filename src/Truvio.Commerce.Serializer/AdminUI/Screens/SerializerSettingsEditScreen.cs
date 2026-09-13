using Truvio.Commerce.Serializer.AdminUI.Commands;
using Truvio.Commerce.Serializer.AdminUI.Models;
using Dynamicweb.CoreUI.Actions;
using Dynamicweb.CoreUI.Actions.Implementations;
using Dynamicweb.CoreUI.Data;
using Dynamicweb.CoreUI.Editors;
using Dynamicweb.CoreUI.Editors.Lists;
using Dynamicweb.CoreUI.Icons;
using Dynamicweb.CoreUI.Screens;
using static Dynamicweb.CoreUI.Editors.Inputs.ListBase;

namespace Truvio.Commerce.Serializer.AdminUI.Screens;

public sealed class SerializerSettingsEditScreen : EditScreenBase<SerializerSettingsModel>
{
    protected override void BuildEditScreen()
    {
        AddComponents("Settings",
        [
            new("Serialize",
            [
                EditorFor(m => m.OutputDirectory),
                EditorFor(m => m.ReplaceOutputSubfolder),
                EditorFor(m => m.MergeOutputSubfolder),
                EditorFor(m => m.ShowReplaceIndicators),
                EditorFor(m => m.ShowMergeIndicators)
            ]),
            new("Information",
            [
                EditorFor(m => m.ConfigFilePath),
                EditorFor(m => m.LastRunsSummary),
                EditorFor(m => m.CoverageSummary),
                EditorFor(m => m.ItemTypeExcludesSummary),
                EditorFor(m => m.XmlExcludesSummary),
                EditorFor(m => m.PredicatesSummary)
            ])
        ]);
    }

    protected override IEnumerable<ActionGroup>? GetScreenActions()
    {
        // First-run: no config or no predicates — getting started is the only sensible
        // action, so the sync actions make way for it (running them would just error).
        if (Model?.NeedsSetup == true)
        {
            return new[]
            {
                new ActionGroup
                {
                    Name = "Get started",
                    Nodes = new List<ActionNode>
                    {
                        new()
                        {
                            Name = "Start from the Swift starter…",
                            Icon = Icon.Rocket,
                            NodeAction = OpenSlideOverAction.To<StarterConfigScreen>()
                                .With(new Truvio.Commerce.Serializer.AdminUI.Queries.StarterConfigQuery())
                        },
                        new()
                        {
                            Name = "Create empty configuration",
                            Icon = Icon.FileAlt,
                            NodeAction = RunCommandAction.For(new CreateEmptyConfigCommand()).WithReloadOnSuccess()
                        }
                    }
                }
            };
        }

        // One verb set, mirrored per mode: Serialize / Preview deserialize / Deserialize,
        // each suffixed with its mode. One preview term ("preview"); the dry-run mechanics
        // live in the command, not the label.
        return new[]
        {
            new ActionGroup
            {
                Name = "Replace",
                Nodes = new List<ActionNode>
                {
                    new()
                    {
                        Name = "Serialize (Replace)",
                        Icon = Icon.DownloadAlt,
                        NodeAction = RunCommandAction.For(new SerializeCommand { Mode = "replace" }).WithReloadOnSuccess()
                    },
                    new()
                    {
                        // Preview: full pipeline, nothing written — the answer to "what
                        // would happen if I deserialized right now?" before committing.
                        Name = "Preview deserialize (Replace)",
                        Icon = Icon.Eye,
                        NodeAction = RunCommandAction.For(new DeserializeCommand { Mode = "replace", IsAdminUiInvocation = true, IsDryRun = true })
                    },
                    new()
                    {
                        Name = "Deserialize (Replace)",
                        Icon = Icon.UploadAlt,
                        // Phase 37-04 D-16: admin UI is the interactive entry point — flip
                        // IsAdminUiInvocation so the resolver falls back to AdminUi default (OFF).
                        NodeAction = RunCommandAction.For(new DeserializeCommand { Mode = "replace", IsAdminUiInvocation = true }).WithReloadOnSuccess()
                    }
                }
            },
            // Merge requires explicit opt-in — expose via a dedicated action group
            // so admins can't trigger a destination-wins deserialize by accident.
            new ActionGroup
            {
                Name = "Merge",
                Nodes = new List<ActionNode>
                {
                    new()
                    {
                        Name = "Serialize (Merge)",
                        Icon = Icon.DownloadAlt,
                        NodeAction = RunCommandAction.For(new SerializeCommand { Mode = "merge" }).WithReloadOnSuccess()
                    },
                    new()
                    {
                        Name = "Preview deserialize (Merge)",
                        Icon = Icon.Eye,
                        NodeAction = RunCommandAction.For(new DeserializeCommand { Mode = "merge", IsAdminUiInvocation = true, IsDryRun = true })
                    },
                    new()
                    {
                        Name = "Deserialize (Merge)",
                        Icon = Icon.UploadAlt,
                        // Phase 37-04 D-16: admin UI triggered — resolver uses AdminUi default (OFF).
                        NodeAction = RunCommandAction.For(new DeserializeCommand { Mode = "merge", IsAdminUiInvocation = true }).WithReloadOnSuccess()
                    }
                }
            },
            // Explicit grant surface for the package functions: opens DW's standard
            // permission management screen scoped to the function's permission entity.
            new ActionGroup
            {
                Name = "Permissions",
                Nodes = new List<ActionNode>
                {
                    new()
                    {
                        Name = "Download Package permissions",
                        Icon = Icon.Lock,
                        NodeAction = NavigateScreenAction
                            .To<Dynamicweb.Application.UI.Screens.PermissionListScreen>()
                            .With(new Dynamicweb.Application.UI.Queries.PermissionsByIdentifierQuery
                            {
                                Key = AdminUI.Security.PackagePermissionEntity.DownloadKey,
                                Name = AdminUI.Security.PackagePermissionEntity.PermissionName,
                                SubName = "Download Package"
                            })
                    },
                    new()
                    {
                        Name = "Upload Package permissions",
                        Icon = Icon.Lock,
                        NodeAction = NavigateScreenAction
                            .To<Dynamicweb.Application.UI.Screens.PermissionListScreen>()
                            .With(new Dynamicweb.Application.UI.Queries.PermissionsByIdentifierQuery
                            {
                                Key = AdminUI.Security.PackagePermissionEntity.UploadKey,
                                Name = AdminUI.Security.PackagePermissionEntity.PermissionName,
                                SubName = "Upload Package"
                            })
                    }
                }
            }
        };
    }

    protected override string GetScreenName() => "Serialize Settings";
    protected override CommandBase<SerializerSettingsModel> GetSaveCommand() => new SaveSerializerSettingsCommand();
}
