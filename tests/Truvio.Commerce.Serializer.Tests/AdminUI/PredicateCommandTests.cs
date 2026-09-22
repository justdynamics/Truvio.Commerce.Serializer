using System.Reflection;
using Truvio.Commerce.Serializer.AdminUI.Commands;
using Truvio.Commerce.Serializer.AdminUI.Models;
using Truvio.Commerce.Serializer.AdminUI.Queries;
using Truvio.Commerce.Serializer.AdminUI.Screens;
using Truvio.Commerce.Serializer.Configuration;
using Truvio.Commerce.Serializer.Models;
using Truvio.Commerce.Serializer.Tests.TestHelpers;
using Dynamicweb.CoreUI.Data;
using Dynamicweb.CoreUI.Editors;
using Dynamicweb.CoreUI.Editors.Lists;
using Xunit;

namespace Truvio.Commerce.Serializer.Tests.AdminUI;

public class PredicateCommandTests : ConfigLoaderValidatorFixtureBase
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public PredicateCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PredCmdTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "Serializer.config.json");
    }

    public override void Dispose()
    {
        base.Dispose();  // clear AsyncLocal first
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private void CreateMergeConfig(List<ProviderPredicateDefinition>? predicates = null)
    {
        // Phase 40 D-01: flat predicate list with explicit per-predicate Mode.
        var config = new SerializerConfiguration
        {
            OutputDirectory = @"\System\Serializer",
            Predicates = predicates ?? new List<ProviderPredicateDefinition>
            {
                new() { Name = "Default", Mode = SerializerMode.Replace, ProviderType = "Content", Path = "/", AreaId = 1, PageId = 10 }
            }
        };
        ConfigWriter.Save(config, _configPath);
    }

    // -------------------------------------------------------------------------
    // SavePredicateCommand tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Save_NullModel_ReturnsInvalid()
    {
        var cmd = new SavePredicateCommand { Model = null };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Invalid, result.Status);
        Assert.Contains("Model data must be given", result.Message);
    }

    [Fact]
    public void Save_EmptyName_ReturnsInvalid()
    {
        var cmd = new SavePredicateCommand
        {
            Model = new PredicateEditModel
            {
                Name = "",
                ProviderType = "Content",
                AreaId = 1,
                PageId = 10
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Invalid, result.Status);
        Assert.Contains("Name", result.Message);
    }

    [Fact]
    public void Save_DuplicateName_ReturnsInvalid()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>
        {
            new() { Name = "Existing", Mode = SerializerMode.Replace, ProviderType = "Content", Path = "/existing", AreaId = 1, PageId = 10 }
        });

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "Existing",
                ProviderType = "Content",
                AreaId = 1,
                PageId = 10
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Invalid, result.Status);
        Assert.Contains("duplicate", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_IndexOutOfRange_ReturnsError()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>
        {
            new() { Name = "Only", Mode = SerializerMode.Replace, ProviderType = "Content", Path = "/only", AreaId = 1, PageId = 10 }
        });

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = 5,
                Name = "Updated",
                ProviderType = "Content",
                AreaId = 1,
                PageId = 10
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Error, result.Status);
    }

    [Fact]
    public void Save_NewPredicate_AppendsToConfig()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>
        {
            new() { Name = "Existing", Mode = SerializerMode.Replace, ProviderType = "Content", Path = "/existing", AreaId = 1, PageId = 10 }
        });

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "New Predicate",
                ProviderType = "Content",
                AreaId = 2,
                PageId = 20,
                Excludes = new() { " path1 ", "path2", "", "path3" }
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        Assert.Equal(2, config.Predicates.Count);
        Assert.Equal("New Predicate", config.Predicates[1].Name);
        Assert.Equal("Content", config.Predicates[1].ProviderType);
        Assert.Equal(3, config.Predicates[1].Excludes.Count);
        Assert.Equal("path1", config.Predicates[1].Excludes[0]);
        Assert.Equal("path2", config.Predicates[1].Excludes[1]);
        Assert.Equal("path3", config.Predicates[1].Excludes[2]);
    }

    [Fact]
    public void Save_UpdateExisting_ReplacesAtIndex()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>
        {
            new() { Name = "First", Mode = SerializerMode.Replace, ProviderType = "Content", Path = "/first", AreaId = 1, PageId = 10 },
            new() { Name = "Second", Mode = SerializerMode.Replace, ProviderType = "Content", Path = "/second", AreaId = 2, PageId = 20 }
        });

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = 0,
                Name = "Updated First",
                ProviderType = "Content",
                AreaId = 3,
                PageId = 30,
                Excludes = new()
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        Assert.Equal(2, config.Predicates.Count);
        Assert.Equal("Updated First", config.Predicates[0].Name);
        Assert.Equal(3, config.Predicates[0].AreaId);
        Assert.Equal(30, config.Predicates[0].PageId);
    }

    // -------------------------------------------------------------------------
    // Multi-provider SavePredicateCommand tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Save_SqlTable_NewPredicate_PersistsAllFields()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "Order Flows",
                ProviderType = "SqlTable",
                Table = "EcomOrderFlow",
                NameColumn = "OrderFlowName",
                CompareColumns = "",
                ServiceCaches = "Dynamicweb.Ecommerce.Orders.PaymentService\nDynamicweb.Ecommerce.Orders.ShippingService"
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        Assert.Single(config.Predicates);
        var pred = config.Predicates[0];
        Assert.Equal("SqlTable", pred.ProviderType);
        Assert.Equal("EcomOrderFlow", pred.Table);
        Assert.Equal("OrderFlowName", pred.NameColumn);
        Assert.Equal(2, pred.ServiceCaches.Count);
        Assert.Equal("Dynamicweb.Ecommerce.Orders.PaymentService", pred.ServiceCaches[0]);
        Assert.Equal("Dynamicweb.Ecommerce.Orders.ShippingService", pred.ServiceCaches[1]);
    }

    [Fact]
    public void Save_SqlTable_MissingTable_ReturnsInvalid()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "Missing Table",
                ProviderType = "SqlTable",
                Table = ""
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Invalid, result.Status);
        Assert.Contains("Table", result.Message);
    }

    [Fact]
    public void Save_Content_MissingArea_ReturnsInvalid()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "Missing Area",
                ProviderType = "Content",
                AreaId = 0,
                PageId = 10
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Invalid, result.Status);
        Assert.Contains("Area", result.Message);
    }

    [Fact]
    public void Save_Content_NewPredicate_PersistsContentFields()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "Content Pred",
                ProviderType = "Content",
                AreaId = 1,
                PageId = 10,
                Excludes = new() { "/excluded" }
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        Assert.Single(config.Predicates);
        var pred = config.Predicates[0];
        Assert.Equal("Content", pred.ProviderType);
        Assert.Equal(1, pred.AreaId);
        Assert.Equal(10, pred.PageId);
        Assert.Single(pred.Excludes);
        Assert.Null(pred.Table);
        Assert.Null(pred.NameColumn);
        Assert.Null(pred.CompareColumns);
    }

    [Fact]
    public void Save_EmptyProviderType_ReturnsInvalid()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "No Provider",
                ProviderType = ""
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Invalid, result.Status);
        Assert.Contains("Provider Type", result.Message);
    }

    [Fact]
    public void Save_UnknownProviderType_ReturnsInvalid()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "Bad Provider",
                ProviderType = "Unknown"
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Invalid, result.Status);
        Assert.Contains("Unknown provider type", result.Message);
    }

    [Fact]
    public void Save_SqlTable_UpdateExisting_CarriesKeyColumnsAndReplaceStrategy()
    {
        // PR #22 review: the edit screen has no keyColumns / replaceStrategy fields, so an
        // admin-UI save must carry them over from the predicate it replaces, not drop them.
        CreateMergeConfig(new List<ProviderPredicateDefinition>
        {
            new()
            {
                Name = "Order Flows", Mode = SerializerMode.Replace, ProviderType = "SqlTable", Table = "EcomOrderFlow",
                NameColumn = "OrderFlowName", KeyColumns = new List<string> { "OrderFlowName" }, ReplaceStrategy = "truncate"
            }
        });

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = 0,
                Name = "Order Flows",
                Mode = "Replace",
                Table = "EcomOrderFlow",
                NameColumn = "OrderFlowDescription"
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var pred = Assert.Single(ConfigLoader.Load(_configPath).Predicates);
        Assert.Equal("OrderFlowDescription", pred.NameColumn);
        Assert.Equal(new[] { "OrderFlowName" }, pred.KeyColumns);
        Assert.Equal("truncate", pred.ReplaceStrategy);
    }

    [Fact]
    public void Save_SqlTable_UpdateExisting_PreservesProviderType()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>
        {
            new() { Name = "Order Flows", Mode = SerializerMode.Replace, ProviderType = "SqlTable", Table = "EcomOrderFlow", NameColumn = "OrderFlowName" }
        });

        // Attempt to tamper ProviderType on update — D-02 should preserve "SqlTable"
        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = 0,
                Name = "Order Flows Updated",
                ProviderType = "Content", // tampered — should be ignored
                Table = "EcomOrderFlowV2",
                NameColumn = "OrderFlowName"
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        Assert.Single(config.Predicates);
        var pred = config.Predicates[0];
        Assert.Equal("SqlTable", pred.ProviderType); // D-02: locked to original
        Assert.Equal("Order Flows Updated", pred.Name);
        Assert.Equal("EcomOrderFlowV2", pred.Table);
    }

    // -------------------------------------------------------------------------
    // Filtering fields round-trip tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Save_Content_NewPredicate_PersistsExcludeFields()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "Content Filtering",
                ProviderType = "Content",
                AreaId = 1,
                PageId = 10,
                ExcludeFields = new() { "PageNavigationTag", "AreaDomain" },
                ExcludeXmlElements = "sort\npagesize"
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        var pred = config.Predicates[0];
        Assert.Equal(2, pred.ExcludeFields.Count);
        Assert.Equal("PageNavigationTag", pred.ExcludeFields[0]);
        Assert.Equal("AreaDomain", pred.ExcludeFields[1]);
        Assert.Equal(2, pred.ExcludeXmlElements.Count);
        Assert.Equal("sort", pred.ExcludeXmlElements[0]);
        Assert.Equal("pagesize", pred.ExcludeXmlElements[1]);
        Assert.Empty(pred.XmlColumns);
    }

    [Fact]
    public void Save_SqlTable_NewPredicate_PersistsFilteringFields()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "SqlTable Filtering",
                ProviderType = "SqlTable",
                Table = "EcomOrderFlow",
                ExcludeFields = new() { "LastModified" },
                XmlColumns = new() { "ShippingXml", "SettingsXml" },
                ExcludeXmlElements = "cache"
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        var pred = config.Predicates[0];
        Assert.Single(pred.ExcludeFields);
        Assert.Equal("LastModified", pred.ExcludeFields[0]);
        Assert.Equal(2, pred.XmlColumns.Count);
        Assert.Equal("ShippingXml", pred.XmlColumns[0]);
        Assert.Equal("SettingsXml", pred.XmlColumns[1]);
        Assert.Single(pred.ExcludeXmlElements);
        Assert.Equal("cache", pred.ExcludeXmlElements[0]);
    }

    [Fact]
    public void Save_Content_XmlColumnsNotPersisted()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "Content No XmlColumns",
                ProviderType = "Content",
                AreaId = 1,
                PageId = 10,
                XmlColumns = new() { "SomeColumn" }
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        var pred = config.Predicates[0];
        Assert.Empty(pred.XmlColumns);
    }

    // -------------------------------------------------------------------------
    // CheckboxList-style value round-trip tests (33-01)
    // -------------------------------------------------------------------------

    [Fact]
    public void Save_SqlTable_ExcludeFields_RoundTrips()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "ExcludeFields RT",
                ProviderType = "SqlTable",
                Table = "EcomOrderFlow",
                ExcludeFields = new() { "OrderFlowID", "OrderFlowName", "OrderFlowOrderStateID" }
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        var pred = config.Predicates[0];
        Assert.Equal(3, pred.ExcludeFields.Count);
        Assert.Equal("OrderFlowID", pred.ExcludeFields[0]);
        Assert.Equal("OrderFlowName", pred.ExcludeFields[1]);
        Assert.Equal("OrderFlowOrderStateID", pred.ExcludeFields[2]);
    }

    [Fact]
    public void Save_SqlTable_XmlColumns_RoundTrips()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "XmlColumns RT",
                ProviderType = "SqlTable",
                Table = "EcomOrderFlow",
                XmlColumns = new() { "SettingsXml", "ConfigXml" }
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        var pred = config.Predicates[0];
        Assert.Equal(2, pred.XmlColumns.Count);
        Assert.Equal("SettingsXml", pred.XmlColumns[0]);
        Assert.Equal("ConfigXml", pred.XmlColumns[1]);
    }

    [Fact]
    public void Save_SqlTable_EmptyFilteringFields_PersistsAsEmptyLists()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "Empty Filtering",
                ProviderType = "SqlTable",
                Table = "EcomOrderFlow",
                ExcludeFields = new(),
                XmlColumns = new()
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        var pred = config.Predicates[0];
        Assert.Empty(pred.ExcludeFields);
        Assert.Empty(pred.XmlColumns);
    }

    [Fact]
    public void Save_SqlTable_UpdateExisting_PreservesFilteringFields()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>
        {
            new()
            {
                Name = "Updatable",
                Mode = SerializerMode.Replace,
                ProviderType = "SqlTable",
                Table = "EcomOrderFlow",
                ExcludeFields = new List<string> { "Col1" }
            }
        });

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = 0,
                Name = "Updatable",
                ProviderType = "SqlTable",
                Table = "EcomOrderFlow",
                ExcludeFields = new() { "Col1", "Col2", "Col3" }
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        var pred = config.Predicates[0];
        Assert.Equal(3, pred.ExcludeFields.Count);
        Assert.Contains("Col1", pred.ExcludeFields);
        Assert.Contains("Col2", pred.ExcludeFields);
        Assert.Contains("Col3", pred.ExcludeFields);
    }

    [Fact]
    public void Save_Content_ExcludeFields_StillWorksWithNewlines()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "Content EF",
                ProviderType = "Content",
                AreaId = 1,
                PageId = 10,
                ExcludeFields = new() { "Field1", "Field2" }
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        var pred = config.Predicates[0];
        Assert.Equal(2, pred.ExcludeFields.Count);
        Assert.Equal("Field1", pred.ExcludeFields[0]);
        Assert.Equal("Field2", pred.ExcludeFields[1]);
    }

    // -------------------------------------------------------------------------
    // DeletePredicateCommand tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Delete_ValidIndex_RemovesPredicate()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>
        {
            new() { Name = "First", Mode = SerializerMode.Replace, ProviderType = "Content", Path = "/first", AreaId = 1, PageId = 10 },
            new() { Name = "Second", Mode = SerializerMode.Replace, ProviderType = "Content", Path = "/second", AreaId = 2, PageId = 20 }
        });

        var cmd = new DeletePredicateCommand
        {
            ConfigPath = _configPath,
            Index = 0
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        Assert.Single(config.Predicates);
        Assert.Equal("Second", config.Predicates[0].Name);
    }

    [Fact]
    public void Delete_NegativeIndex_ReturnsError()
    {
        CreateMergeConfig();

        var cmd = new DeletePredicateCommand
        {
            ConfigPath = _configPath,
            Index = -1
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Error, result.Status);
        Assert.Contains("Invalid", result.Message);
    }

    [Fact]
    public void Delete_IndexOutOfRange_ReturnsError()
    {
        CreateMergeConfig();

        var cmd = new DeletePredicateCommand
        {
            ConfigPath = _configPath,
            Index = 99
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Error, result.Status);
    }

    [Fact]
    public void Delete_LastPredicate_ResultsInEmptyList()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>
        {
            new() { Name = "Only", Mode = SerializerMode.Replace, ProviderType = "Content", Path = "/only", AreaId = 1, PageId = 10 }
        });

        var cmd = new DeletePredicateCommand
        {
            ConfigPath = _configPath,
            Index = 0
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        Assert.Empty(config.Predicates);
    }

    // -------------------------------------------------------------------------
    // Phase 40 D-01: SavePredicateCommand persists per-predicate Mode in flat list
    // -------------------------------------------------------------------------

    [Fact]
    public void Save_PredicateInReplaceMode_AppendsToFlatListWithReplaceMode()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = -1,
                Mode = nameof(SerializerMode.Replace),  // Phase 41 D-13: string-typed model
                Name = "Replace1",
                ProviderType = "Content",
                AreaId = 1,
                PageId = 10
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        Assert.Single(config.Predicates);
        Assert.Equal("Replace1", config.Predicates[0].Name);
        Assert.Equal(SerializerMode.Replace, config.Predicates[0].Mode);
    }

    [Fact]
    public void Save_PredicateInMergeMode_AppendsToFlatListWithMergeMode()
    {
        // Merge a config with one existing Replace predicate so we can prove Merge.Save doesn't overwrite Replace.
        CreateMergeConfig(new List<ProviderPredicateDefinition>
        {
            new() { Name = "ReplaceExisting", Mode = SerializerMode.Replace, ProviderType = "Content", Path = "/d", AreaId = 1, PageId = 10 }
        });

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            Model = new PredicateEditModel
            {
                Index = -1,
                Mode = nameof(SerializerMode.Merge),  // Phase 41 D-13: string-typed model
                Name = "Merge1",
                ProviderType = "Content",
                AreaId = 1,
                PageId = 10
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        Assert.Equal(2, config.Predicates.Count);
        var replace = config.Predicates.Where(p => p.Mode == SerializerMode.Replace).ToList();
        var merge = config.Predicates.Where(p => p.Mode == SerializerMode.Merge).ToList();
        Assert.Single(replace);
        Assert.Equal("ReplaceExisting", replace[0].Name);
        Assert.Single(merge);
        Assert.Equal("Merge1", merge[0].Name);
    }

    // -------------------------------------------------------------------------
    // Phase 37-03: WhereClause + IncludeFields round-trip + validation
    // -------------------------------------------------------------------------

    [Fact]
    public void Save_SqlTable_WhereClauseAndIncludeFields_RoundTrip()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        // Validator must be bypassed for this unit test (no live DB). SavePredicateCommand
        // exposes IdentifierValidator / WhereValidator hooks that default to production
        // validators; tests inject fixture validators that accept anything.
        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            IdentifierValidator = new SqlIdentifierValidator(
                tableLoader: () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AccessUser" },
                columnLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "AccessUserType", "AccessUserUserName", "AccessUserHostingName"
                }),
            WhereValidator = new SqlWhereClauseValidator(),
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "AU-Roles",
                ProviderType = "SqlTable",
                Table = "AccessUser",
                WhereClause = "AccessUserType = 2 AND AccessUserUserName IN ('Admin','Editors')",
                IncludeFields = new() { "AccessUserHostingName" }
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        var pred = config.Predicates[0];
        Assert.Equal("AccessUserType = 2 AND AccessUserUserName IN ('Admin','Editors')", pred.Where);
        Assert.Single(pred.IncludeFields);
        Assert.Equal("AccessUserHostingName", pred.IncludeFields[0]);
    }

    [Fact]
    public void Save_InvalidWhereClause_ReturnsInvalid()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            IdentifierValidator = new SqlIdentifierValidator(
                tableLoader: () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AccessUser" },
                columnLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AccessUserType" }),
            WhereValidator = new SqlWhereClauseValidator(),
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "BadWhere",
                ProviderType = "SqlTable",
                Table = "AccessUser",
                WhereClause = "AccessUserType = 2; DROP TABLE X"
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Invalid, result.Status);
        Assert.Contains(";", result.Message);
    }

    [Fact]
    public void Save_InvalidTableIdentifier_ReturnsInvalid()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            IdentifierValidator = new SqlIdentifierValidator(
                tableLoader: () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AccessUser" },
                columnLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AccessUserType" }),
            WhereValidator = new SqlWhereClauseValidator(),
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "BadTable",
                ProviderType = "SqlTable",
                Table = "NotARealTable"
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Invalid, result.Status);
        Assert.Contains("INFORMATION_SCHEMA", result.Message);
    }

    [Fact]
    public void Save_InvalidIncludeFieldIdentifier_ReturnsInvalid()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var cmd = new SavePredicateCommand
        {
            ConfigPath = _configPath,
            IdentifierValidator = new SqlIdentifierValidator(
                tableLoader: () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AccessUser" },
                columnLoader: _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AccessUserType" }),
            WhereValidator = new SqlWhereClauseValidator(),
            Model = new PredicateEditModel
            {
                Index = -1,
                Name = "BadInclude",
                ProviderType = "SqlTable",
                Table = "AccessUser",
                IncludeFields = new() { "NonexistentColumn" }
            }
        };

        var result = cmd.Handle();

        Assert.Equal(CommandResult.ResultType.Invalid, result.Status);
        Assert.Contains("INFORMATION_SCHEMA", result.Message);
    }

    [Fact]
    public void Delete_PredicateInMergeMode_RemovesByIndexFromFlatList()
    {
        // Merge a config with one Replace + one Merge predicate.
        CreateMergeConfig(new List<ProviderPredicateDefinition>
        {
            new() { Name = "ReplaceExisting", Mode = SerializerMode.Replace, ProviderType = "Content", Path = "/d", AreaId = 1, PageId = 10 },
            new() { Name = "MergeOnly", Mode = SerializerMode.Merge, ProviderType = "Content", Path = "/d", AreaId = 1, PageId = 10 }
        });

        // Phase 40 D-01: delete is index-based on the flat list — caller passes the flat-list index.
        var del = new DeletePredicateCommand
        {
            ConfigPath = _configPath,
            Index = 1  // MergeOnly is at flat-list index 1
        };

        var result = del.Handle();

        Assert.Equal(CommandResult.ResultType.Ok, result.Status);
        var config = ConfigLoader.Load(_configPath);
        Assert.Single(config.Predicates);
        Assert.Equal("ReplaceExisting", config.Predicates[0].Name);
        Assert.Equal(SerializerMode.Replace, config.Predicates[0].Mode);
    }

    // -------------------------------------------------------------------------
    // Phase 41 D-13 + D-12 + D-11 RED tests: Mode property must become string-typed,
    // ConfigurableProperty must carry hint text, PredicateEditScreen Mode-option
    // labels must drop the parenthetical suffixes. Tests use reflection so the file
    // stays compilable while Mode is still enum-typed; assertions encode the GREEN
    // target so Plan 41-03 must turn them GREEN.
    // -------------------------------------------------------------------------

    [Fact]
    public void ModeProperty_IsString_NotEnum_PostPhase41()
    {
        // Phase 41 D-13: Mode must be string-typed (matches LogLevel/ConflictStrategy precedent).
        // Will FAIL until Plan 41-03 lands.
        var modeProp = typeof(PredicateEditModel).GetProperty("Mode");
        Assert.NotNull(modeProp);
        Assert.Equal(typeof(string), modeProp!.PropertyType);
    }

    [Fact]
    public void ModeProperty_HasHint_WithExplanatoryCopy_PostPhase41()
    {
        // Phase 41 D-12: tooltip-style copy moves to `hint:` parameter (not `explanation:`).
        // Assert via stable substrings — the wording is allowed minor evolution.
        var modeProp = typeof(PredicateEditModel).GetProperty("Mode");
        Assert.NotNull(modeProp);
        var attr = modeProp!.GetCustomAttribute<ConfigurablePropertyAttribute>();
        Assert.NotNull(attr);
        var hint = attr!.Hint ?? string.Empty;
        Assert.Contains("Replace =", hint);
        Assert.Contains("Merge =", hint);
        Assert.Contains("source-wins", hint);
        Assert.Contains("field-level fill", hint);
    }

    [Fact]
    public void Save_ModeAsString_Replace_RoundTripsViaQuery_PostPhase41()
    {
        // Phase 41 D-13: SavePredicateCommand parses string→enum on save; PredicateByIndexQuery
        // returns string on hydrate. Round-trip must preserve "Replace" verbatim.
        // RED today: Mode is enum-typed so the round-trip yields SerializerMode.Replace, not "Replace".
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var savedOverride = ConfigPathResolver.TestOverridePath;
        ConfigPathResolver.TestOverridePath = _configPath;
        try
        {
            var model = new PredicateEditModel { Index = -1, Name = "Default", ProviderType = "Content", AreaId = 1, PageId = 10 };
            // Mode set via reflection to keep this test compilable while the property is still enum-typed.
            var modeProp = typeof(PredicateEditModel).GetProperty("Mode")!;
            if (modeProp.PropertyType == typeof(string))
                modeProp.SetValue(model, "Replace");
            else
                modeProp.SetValue(model, Enum.Parse(typeof(SerializerMode), "Replace"));

            var saveCmd = new SavePredicateCommand { ConfigPath = _configPath, Model = model };
            var saveResult = saveCmd.Handle();
            Assert.Equal(CommandResult.ResultType.Ok, saveResult.Status);

            var query = new PredicateByIndexQuery();
            typeof(PredicateByIndexQuery).GetProperty("Index")!.SetValue(query, 0);
            var reloaded = query.GetModel();
            Assert.NotNull(reloaded);

            var reloadedMode = modeProp.GetValue(reloaded);
            // GREEN target: reloadedMode is the string "Replace". RED today: it's SerializerMode.Replace.
            Assert.Equal("Replace", reloadedMode);
        }
        finally
        {
            ConfigPathResolver.TestOverridePath = savedOverride;
        }
    }

    [Fact]
    public void Save_ModeAsString_Merge_RoundTripsViaQuery_PostPhase41()
    {
        CreateMergeConfig(new List<ProviderPredicateDefinition>());

        var savedOverride = ConfigPathResolver.TestOverridePath;
        ConfigPathResolver.TestOverridePath = _configPath;
        try
        {
            var model = new PredicateEditModel { Index = -1, Name = "Default", ProviderType = "Content", AreaId = 1, PageId = 10 };
            var modeProp = typeof(PredicateEditModel).GetProperty("Mode")!;
            if (modeProp.PropertyType == typeof(string))
                modeProp.SetValue(model, "Merge");
            else
                modeProp.SetValue(model, Enum.Parse(typeof(SerializerMode), "Merge"));

            var saveCmd = new SavePredicateCommand { ConfigPath = _configPath, Model = model };
            Assert.Equal(CommandResult.ResultType.Ok, saveCmd.Handle().Status);

            var query = new PredicateByIndexQuery();
            typeof(PredicateByIndexQuery).GetProperty("Index")!.SetValue(query, 0);
            var reloaded = query.GetModel();
            Assert.NotNull(reloaded);
            Assert.Equal("Merge", modeProp.GetValue(reloaded));
        }
        finally
        {
            ConfigPathResolver.TestOverridePath = savedOverride;
        }
    }

    [Fact]
    public void Save_ModeAsString_BogusValue_ReturnsInvalid_PostPhase41()
    {
        // Phase 41 D-13 + threat model T-41-01: invalid Mode strings rejected by SavePredicateCommand
        // with CommandResult.ResultType.Invalid (mirrors ConfigLoader's case-insensitive Enum.TryParse
        // pre-validation). RED today: when Mode is enum, you can't even ASSIGN "BogusMode" — the test
        // proves the post-fix invariant. Skips early until model is string-typed.
        CreateMergeConfig(new List<ProviderPredicateDefinition>());
        var model = new PredicateEditModel { Index = -1, Name = "Default", ProviderType = "Content", AreaId = 1, PageId = 10 };
        var modeProp = typeof(PredicateEditModel).GetProperty("Mode")!;
        if (modeProp.PropertyType != typeof(string))
        {
            // RED today: this short-circuits while Mode is enum. Becomes a real RED assertion
            // once Plan 41-03 lands the string-typed Mode and the test reaches SavePredicateCommand.
            return;
        }
        modeProp.SetValue(model, "BogusMode");

        var saveCmd = new SavePredicateCommand { ConfigPath = _configPath, Model = model };
        var result = saveCmd.Handle();

        Assert.Equal(CommandResult.ResultType.Invalid, result.Status);
        Assert.Contains("Mode", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            result.Message.Contains("Replace", StringComparison.OrdinalIgnoreCase) ||
            result.Message.Contains("Merge", StringComparison.OrdinalIgnoreCase),
            $"Expected error message to name the valid values; got: {result.Message}");
    }

    [Fact]
    public void PredicateEditScreen_ModeOptions_HaveCleanLabels_PostPhase41()
    {
        // Phase 41 D-11: option labels lose the parenthetical "(source-wins)" /
        // "(field-level merge)" suffixes.
        var screen = new PredicateEditScreen();
        var getEditor = typeof(PredicateEditScreen)
            .GetMethod("GetEditor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var editor = (EditorBase?)getEditor.Invoke(screen, new object[] { "Mode" });
        Assert.NotNull(editor);
        var select = Assert.IsType<Select>(editor);
        Assert.NotNull(select.Options);
        Assert.Contains(select.Options!, o => (o.Value as string) == "Replace" && o.Label == "Replace");
        Assert.Contains(select.Options!, o => (o.Value as string) == "Merge" && o.Label == "Merge");
        // Reject parens-suffix labels explicitly.
        Assert.DoesNotContain(select.Options!, o => o.Label != null && o.Label.Contains("("));
    }
}
