-- Epi Info metadata tables, generated from MetadataDbProvider.CreateTables() (SQL Server path).
USE [EpiRehearsal];
GO
CREATE TABLE [dbo].[metaBackgrounds] (
    [BackgroundId] int NOT NULL IDENTITY CONSTRAINT [PK_BackgroundId_metaBackgrounds] PRIMARY KEY,
    [ImageId] int NULL,
    [ImageLayout] nvarchar(10) NULL,
    [Color] int NULL
);
GO
CREATE TABLE [dbo].[metaDataTypes] (
    [DataTypeId] int NOT NULL CONSTRAINT [PK_DataTypeId_metaDataTypes] PRIMARY KEY,
    [HasPattern] bit NOT NULL,
    [HasSize] bit NOT NULL,
    [HasRange] bit NOT NULL,
    [Name] nvarchar(30) NOT NULL
);
GO
CREATE TABLE [dbo].[metaDbInfo] (
    [ProjectId] uniqueidentifier NOT NULL,
    [ProjectLocation] nvarchar(255) NOT NULL,
    [ProjectName] nvarchar(64) NOT NULL,
    [EpiVersion] nvarchar(20) NOT NULL,
    [Purpose] int NOT NULL
);
GO
CREATE TABLE [dbo].[metaFieldTypes] (
    [FieldTypeId] int NOT NULL CONSTRAINT [PK_FieldTypeId_metaFieldTypes] PRIMARY KEY,
    [Name] nvarchar(50) NOT NULL,
    [HasFont] bit NOT NULL,
    [HasRepeatLast] bit NOT NULL,
    [HasRequired] bit NOT NULL,
    [HasReadOnly] bit NOT NULL,
    [HasRetainImageSize] bit NOT NULL,
    [IsDropDown] bit NOT NULL,
    [IsGridColumn] bit NOT NULL,
    [IsSystem] bit NOT NULL,
    [DefaultPatternId] int NOT NULL,
    [DataTypeId] int NOT NULL REFERENCES [metaDataTypes]([DataTypeId])
);
GO
CREATE TABLE [dbo].[metaImages] (
    [ImageId] int NOT NULL IDENTITY CONSTRAINT [PK_ImageId_metaImages] PRIMARY KEY,
    [Image] image NULL,
    [ImageUniqueValue] int NULL
);
GO
CREATE TABLE [dbo].[metaLayerRenderTypes] (
    [LayerRenderTypeId] int NOT NULL CONSTRAINT [PK_LayerRenderTypeId_metaLayerRenderTypes] PRIMARY KEY,
    [Name] nvarchar(30) NOT NULL
);
GO
CREATE TABLE [dbo].[metaLayers] (
    [LayerId] int NOT NULL IDENTITY CONSTRAINT [PK_LayerId_metaLayers] PRIMARY KEY,
    [Gml] ntext NULL,
    [GmlSchema] ntext NULL,
    [Name] nvarchar(64) NOT NULL,
    [Description] nvarchar(255) NULL,
    [FileName] nvarchar NULL
);
GO
CREATE TABLE [dbo].[metaMaps] (
    [MapId] int NOT NULL IDENTITY CONSTRAINT [PK_MapId_metaMaps] PRIMARY KEY,
    [Name] nvarchar(64) NOT NULL,
    [Title] nvarchar(255) NULL
);
GO
CREATE TABLE [dbo].[metaPatterns] (
    [PatternId] int NOT NULL CONSTRAINT [PK_PatternId_metaPatterns] PRIMARY KEY,
    [Expression] nvarchar(30) NOT NULL,
    [Mask] nvarchar(30) NOT NULL,
    [FormattedExpression] nvarchar(30) NOT NULL,
    [DataTypeId] int NOT NULL REFERENCES [metaDataTypes]([DataTypeId])
);
GO
CREATE TABLE [dbo].[metaPrograms] (
    [ProgramId] int NOT NULL IDENTITY CONSTRAINT [PK_ProgramId_metaPrograms] PRIMARY KEY,
    [Name] nvarchar(64) NOT NULL,
    [Content] ntext NOT NULL,
    [Comment] ntext NULL,
    [DateCreated] datetime NOT NULL,
    [DateModified] datetime NOT NULL,
    [Author] nvarchar(64) NULL
);
GO
CREATE TABLE [dbo].[metaViews] (
    [ViewId] int NOT NULL IDENTITY CONSTRAINT [PK_ViewId_metaViews] PRIMARY KEY,
    [Name] nvarchar(64) NOT NULL,
    [IsRelatedView] bit NOT NULL,
    [CheckCode] ntext NULL,
    [CheckCodeBefore] ntext NULL,
    [CheckCodeAfter] ntext NULL,
    [RecordCheckCodeBefore] ntext NULL,
    [RecordCheckCodeAfter] ntext NULL,
    [CheckCodeVariableDefinitions] ntext NULL,
    [Width] int NULL,
    [Height] int NULL,
    [Orientation] nvarchar(16) NOT NULL,
    [LabelAlign] nvarchar(16) NOT NULL,
    [EIWSOrganizationKey] nvarchar(64) NULL,
    [EIWSFormId] nvarchar(64) NULL,
    [EWEOrganizationKey] nvarchar(64) NULL,
    [EWEFormId] nvarchar(64) NULL
);
GO
CREATE TABLE [dbo].[metaMapLayers] (
    [MapLayerId] int NOT NULL IDENTITY CONSTRAINT [PK_MapLayerId_metaMapLayers] PRIMARY KEY,
    [RenderField] nvarchar(64) NULL,
    [MarkerColor] int NULL,
    [RampBeginColor] int NULL,
    [RampEndColor] int NULL,
    [ClassBreaks] int NULL,
    [DataTableName] nvarchar(64) NULL,
    [DataTableKey] nvarchar(64) NULL,
    [FeatureKey] nvarchar(64) NULL,
    [LineColor] int NULL,
    [FillColor] int NULL,
    [PolygonOutlineColor] int NULL,
    [MapId] int NOT NULL REFERENCES [metaMaps]([MapId]),
    [LayerId] int NOT NULL REFERENCES [metaLayers]([LayerId]),
    [LayerRenderTypeId] int NOT NULL REFERENCES [metaLayerRenderTypes]([LayerRenderTypeId])
);
GO
CREATE TABLE [dbo].[metaMapPoints] (
    [MapPointId] int NOT NULL IDENTITY CONSTRAINT [PK_MapPointId_metaMapPoints] PRIMARY KEY,
    [DataSourceTableName] nvarchar(64) NOT NULL,
    [DataSourceXCoordinateColumnName] nvarchar(64) NOT NULL,
    [DataSourceYCoordinateColumnName] nvarchar(64) NOT NULL,
    [DataSourceLabelColumnName] nvarchar(64) NULL,
    [Size] int NOT NULL,
    [Color] int NOT NULL,
    [MapId] int NOT NULL REFERENCES [metaMaps]([MapId])
);
GO
CREATE TABLE [dbo].[metaPages] (
    [PageId] int NOT NULL IDENTITY CONSTRAINT [PK_PageId_metaPages] PRIMARY KEY,
    [Name] nvarchar(64) NOT NULL,
    [Position] smallint NOT NULL,
    [CheckCodeBefore] ntext NULL,
    [CheckCodeAfter] ntext NULL,
    [BackgroundId] int NOT NULL,
    [ViewId] int NOT NULL REFERENCES [metaViews]([ViewId]) ON DELETE CASCADE
);
GO
CREATE TABLE [dbo].[metaFields] (
    [FieldId] int NOT NULL IDENTITY CONSTRAINT [PK_FieldId_metaFields] PRIMARY KEY,
    [UniqueId] uniqueidentifier NULL,
    [Name] nvarchar(64) NOT NULL,
    [PromptText] ntext NULL,
    [ControlFontFamily] nvarchar(127) NULL,
    [ControlFontStyle] nvarchar(50) NULL,
    [ControlFontSize] decimal(2) NULL,
    [ControlTopPositionPercentage] float NULL,
    [ControlLeftPositionPercentage] float NULL,
    [ControlHeightPercentage] float NULL,
    [ControlWidthPercentage] float NULL,
    [TabIndex] decimal NULL,
    [HasTabStop] bit NULL,
    [PromptFontFamily] nvarchar(127) NULL,
    [PromptFontStyle] nvarchar(50) NULL,
    [PromptFontSize] decimal(2) NULL,
    [PromptScriptName] nvarchar(20) NULL,
    [PromptTopPositionPercentage] float NULL,
    [PromptLeftPositionPercentage] float NULL,
    [ControlScriptName] nvarchar(20) NULL,
    [ShouldRepeatLast] bit NULL,
    [IsRequired] bit NULL,
    [IsReadOnly] bit NULL,
    [IsEncrypted] bit NULL,
    [ShouldRetainImageSize] bit NULL,
    [MaxLength] smallint NULL,
    [Lower] nvarchar(20) NULL,
    [Upper] nvarchar(20) NULL,
    [Pattern] nvarchar(50) NULL,
    [ShowTextOnRight] bit NULL,
    [CheckCodeBefore] ntext NULL,
    [CheckCodeAfter] ntext NULL,
    [RelateCondition] ntext NULL,
    [ShouldReturnToParent] bit NULL,
    [SourceTableName] nvarchar(255) NULL,
    [CodeColumnName] nvarchar(255) NULL,
    [TextColumnName] nvarchar(255) NULL,
    [Sort] bit NULL,
    [IsExclusiveTable] bit NULL,
    [DataTableName] nvarchar(64) NULL,
    [SourceFieldId] int NULL,
    [FieldTypeId] int NOT NULL REFERENCES [metaFieldTypes]([FieldTypeId]),
    [RelatedViewId] int NULL REFERENCES [metaViews]([ViewId]),
    [List] ntext NULL,
    [BackgroundColor] int NULL,
    [ViewId] int NOT NULL REFERENCES [metaViews]([ViewId]) ON DELETE CASCADE,
    [PageId] int NULL REFERENCES [metaPages]([PageId])
);
GO
CREATE TABLE [dbo].[metaGridColumns] (
    [GridColumnId] int NOT NULL IDENTITY CONSTRAINT [PK_GridColumnId_metaGridColumns] PRIMARY KEY,
    [Name] nvarchar(64) NOT NULL,
    [Size] smallint NULL,
    [Position] smallint NOT NULL,
    [Text] nvarchar(64) NOT NULL,
    [ShouldRepeatLast] bit NOT NULL,
    [IsRequired] bit NULL,
    [IsReadOnly] bit NULL,
    [Pattern] nvarchar(64) NULL,
    [Upper] nvarchar(20) NULL,
    [Lower] nvarchar(20) NULL,
    [Width] int NOT NULL,
    [SourceTableName] nvarchar(50) NULL,
    [CodeColumnName] nvarchar(50) NULL,
    [TextColumnName] nvarchar(50) NULL,
    [Sort] bit NULL,
    [IsExclusiveTable] bit NULL,
    [DataTableName] nvarchar(64) NULL,
    [FieldId] int NOT NULL,
    [FieldTypeId] int NOT NULL,
    [IsUniqueField] bit NULL
);
GO
CREATE TABLE [dbo].[metaLinks] (
    [LinkId] int NOT NULL IDENTITY CONSTRAINT [PK_LinkId_metaLinks] PRIMARY KEY,
    [FromRecordGuid] nvarchar(255) NOT NULL,
    [ToRecordGuid] nvarchar(255) NOT NULL,
    [FromViewId] int NOT NULL,
    [ToViewId] int NOT NULL
);
GO
