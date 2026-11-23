-- Lookup rows Epi Info inserts in code (not in MetadataTableScript.sql). Copied from the sample projects.
USE [EpiRehearsal];
GO
INSERT INTO [dbo].[metaDataTypes] ([DataTypeId], [HasPattern], [HasSize], [HasRange], [Name]) VALUES
(0, 0, 0, 0, N'Object'),
(1, 1, 0, 1, N'Number'),
(2, 0, 1, 0, N'Text'),
(3, 1, 0, 1, N'Date'),
(4, 1, 0, 0, N'Time'),
(5, 1, 0, 0, N'DateTime'),
(6, 0, 0, 0, N'Boolean'),
(7, 1, 0, 0, N'PhoneNumber'),
(8, 0, 0, 0, N'YesNo'),
(9, 0, 0, 0, N'Unknown'),
(10, 0, 0, 0, N'GUID');
GO
INSERT INTO [dbo].[metaFieldTypes] ([FieldTypeId], [Name], [HasFont], [HasRepeatLast], [HasRequired], [HasReadOnly], [HasRetainImageSize], [IsDropDown], [IsGridColumn], [IsSystem], [DefaultPatternId], [DataTypeId]) VALUES
(1, N'Text', 1, 1, 1, 1, 0, 1, 1, 0, 0, 2),
(2, N'Label/Title', 0, 0, 0, 0, 0, 0, 0, 0, 0, 9),
(3, N'Text (Uppercase)', 1, 1, 1, 1, 0, 0, 0, 0, 0, 2),
(4, N'Multiline', 1, 1, 1, 1, 0, 0, 0, 0, 0, 2),
(5, N'Number', 1, 1, 1, 1, 0, 0, 1, 0, 2, 1),
(6, N'Phone Number', 1, 1, 1, 1, 0, 0, 1, 0, 7, 7),
(7, N'Date', 1, 1, 1, 1, 0, 0, 1, 0, 9, 3),
(8, N'Time', 1, 1, 1, 1, 0, 0, 0, 0, 14, 4),
(9, N'Date/Time', 1, 1, 1, 1, 0, 0, 0, 0, 16, 5),
(10, N'Checkbox', 0, 1, 0, 1, 0, 0, 0, 0, 0, 6),
(11, N'Yes/No', 1, 1, 1, 1, 0, 0, 0, 0, 0, 8),
(12, N'Option', 0, 0, 0, 0, 0, 0, 0, 0, 0, 2),
(13, N'Command Button', 1, 0, 0, 0, 0, 0, 0, 0, 0, 9),
(14, N'Image', 0, 0, 0, 0, 1, 0, 0, 0, 0, 0),
(15, N'Mirror', 1, 0, 0, 0, 0, 0, 0, 0, 0, 9),
(16, N'Grid', 0, 0, 0, 0, 0, 0, 0, 0, 0, 9),
(17, N'Legal Values', 1, 1, 1, 1, 0, 1, 1, 0, 0, 9),
(18, N'Codes', 1, 1, 1, 1, 0, 1, 0, 0, 0, 9),
(19, N'Comment Legal', 1, 1, 1, 1, 0, 1, 1, 0, 0, 9),
(20, N'Relate', 1, 0, 0, 0, 0, 0, 0, 0, 0, 9),
(21, N'Group', 0, 0, 0, 0, 0, 0, 0, 0, 0, 9),
(22, N'RecStatus', 0, 0, 0, 0, 0, 0, 0, 1, 0, 1),
(23, N'UniqueKey', 0, 0, 0, 0, 0, 0, 0, 1, 0, 1),
(24, N'ForeignKey', 0, 0, 0, 0, 0, 0, 0, 1, 0, 1),
(25, N'GUID', 0, 0, 0, 0, 0, 0, 0, 0, 0, 10),
(26, N'GlobalRecordId', 0, 0, 0, 0, 0, 0, 0, 0, 0, 2),
(27, N'List', 1, 1, 1, 1, 0, 1, 0, 0, 0, 9),
(99, N'Unknown', 0, 0, 0, 0, 0, 0, 0, 0, 0, 9);
GO
INSERT INTO [dbo].[metaPatterns] ([PatternId], [Expression], [Mask], [FormattedExpression], [DataTypeId]) VALUES
(1, N'#', N'#', N'#', 1),
(2, N'##', N'##', N'##', 1),
(3, N'###', N'###', N'###', 1),
(4, N'####', N'####', N'####', 1),
(5, N'##.##', N'##.##', N'##.##', 1),
(6, N'##.###', N'##.###', N'##.###', 1),
(7, N'###-###-####', N'###-###-####', N'###-###-####', 7),
(8, N'###-####', N'###-####', N'###-####', 7),
(9, N'MM-DD-YYYY', N'##-##-####', N'MM-dd-yyyy', 3),
(10, N'MM-DD', N'##-##', N'MM-dd', 3),
(11, N'DD-MM-YYYY', N'##-##-####', N'dd-MM-yyyy', 3),
(12, N'DD-MM', N'##-##', N'dd-MM', 3),
(13, N'YYYY-MM-DD', N'####-##-##', N'yyyy-MM-dd', 3),
(14, N'HH:MM:SS AMPM', N'##:##:## ??', N'hh:mm:ss tt', 4),
(15, N'HH:MM:SS', N'##:##:##', N'hh:mm:ss', 4),
(16, N'MM-DD-YYYY HH:MM:SS AMPM', N'##-##-#### ##:##:## ??', N'MM-dd-yyyy hh:mm:ss tt', 5),
(17, N'DD-MM-YYYY HH:MM:SS AMPM', N'##-##-#### ##:##:## ??', N'dd-MM-yyyy hh:mm:ss tt', 5),
(18, N'YYYY-MM-DD HH:MM:SS AMPM', N'####-##-## ##:##:## ??', N'yyyy-MM-dd hh:mm:ss tt', 5);
GO
INSERT INTO [dbo].[metaLayerRenderTypes] ([LayerRenderTypeId], [Name]) VALUES
(1, N'Simple'),
(2, N'Choropleth'),
(3, N'Dot Density'),
(4, N'Unique Values');
GO
