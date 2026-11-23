-- Code (lookup) tables used by legal values / comment legal fields.
USE [EpiRehearsal];
GO
CREATE TABLE [dbo].[codeassignedarea] ([assignedarea] nvarchar(255) NOT NULL);
GO
INSERT INTO [dbo].[codeassignedarea] ([assignedarea]) VALUES
(N'Basketball'),
(N'Boys Dorm'),
(N'Clinic'),
(N'Dining'),
(N'Garden'),
(N'Girls Dorm'),
(N'Lake'),
(N'Library'),
(N'Movie'),
(N'Other'),
(N'Picnic Area'),
(N'Pool'),
(N'Ropes Course'),
(N'Tennis');
GO
CREATE TABLE [dbo].[codecamprole] ([camprole] nvarchar(255) NOT NULL);
GO
INSERT INTO [dbo].[codecamprole] ([camprole]) VALUES
(N'Camper'),
(N'Counselor'),
(N'Housemom'),
(N'Staff');
GO
CREATE TABLE [dbo].[codeethnicity] ([ethnicity] nvarchar(255) NOT NULL);
GO
INSERT INTO [dbo].[codeethnicity] ([ethnicity]) VALUES
(N'1- Hispanic/Latino'),
(N'2- Not Hispanic/Latino');
GO
CREATE TABLE [dbo].[codelabresults] ([LabResults] nvarchar(255) NOT NULL);
GO
INSERT INTO [dbo].[codelabresults] ([LabResults]) VALUES
(N'1- Positive'),
(N'2- Negative'),
(N'3- Equivocal'),
(N'9- Not done/Unknown');
GO
CREATE TABLE [dbo].[codeloosestoolsperday] ([loosestoolsperday] nvarchar(255) NOT NULL);
GO
INSERT INTO [dbo].[codeloosestoolsperday] ([loosestoolsperday]) VALUES
(N'1-3 per day'),
(N'4-6 per day'),
(N'7-10 per day'),
(N'10+ per day');
GO
CREATE TABLE [dbo].[codephvs_administrativesex_hl7_2x] ([phvs_administrativesex_hl7_2x] nvarchar(255) NOT NULL);
GO
INSERT INTO [dbo].[codephvs_administrativesex_hl7_2x] ([phvs_administrativesex_hl7_2x]) VALUES
(N'F-Female'),
(N'M-Male'),
(N'U-Unknown');
GO
CREATE TABLE [dbo].[codephvs_ethnicitygroup_cdc] ([phvs_ethnicitygroup_cdc] nvarchar(255) NOT NULL);
GO
INSERT INTO [dbo].[codephvs_ethnicitygroup_cdc] ([phvs_ethnicitygroup_cdc]) VALUES
(N'2135:2-Hispanic or Latino'),
(N'2186:5-Not Hispanic or Latino');
GO
CREATE TABLE [dbo].[coderace] ([race] nvarchar(255) NOT NULL);
GO
INSERT INTO [dbo].[coderace] ([race]) VALUES
(N'1- American Indian/Alaska Native'),
(N'2- Asian'),
(N'3- Black or African American'),
(N'4- Native Hawaiian or Other Pacific Islander'),
(N'5- White');
GO
CREATE TABLE [dbo].[codesex] ([sex] nvarchar(255) NOT NULL);
GO
INSERT INTO [dbo].[codesex] ([sex]) VALUES
(N'1- Male'),
(N'2- Female');
GO
CREATE TABLE [dbo].[codestate] ([State] nvarchar(255) NOT NULL);
GO
INSERT INTO [dbo].[codestate] ([State]) VALUES
(N'AL- Alabama'),
(N'AK- Alaska'),
(N'AZ- Arizona'),
(N'AR- Arkansas'),
(N'CA- California'),
(N'CO- Colorado'),
(N'CT- Connecticut'),
(N'DE- Delaware'),
(N'DC- District of Columbia'),
(N'FL- Florida'),
(N'GA- Georgia'),
(N'HI- Hawaii'),
(N'ID- Idaho'),
(N'IL- Illinois'),
(N'IN- Indiana'),
(N'IA- Iowa'),
(N'KS- Kansas'),
(N'KY- Kentucky'),
(N'LA- Lousiana'),
(N'ME- Maine'),
(N'MD- Maryland'),
(N'MA- Massachusetts'),
(N'MI- Michigan'),
(N'MN- Minnesota'),
(N'MS- Mississippi'),
(N'MO- Missouri'),
(N'MT- Montana'),
(N'NE- Nebraska'),
(N'NV- Nevada'),
(N'NH- New Hampshire'),
(N'NJ- New Jersey'),
(N'NM- New Mexico'),
(N'NY- New York'),
(N'NC- North Carolina'),
(N'ND- North Dakota'),
(N'OH- Ohio'),
(N'OK- Oklahoma'),
(N'OR- Oregon'),
(N'PA- Pennsylvania'),
(N'RI- Rhode Island'),
(N'SC- South Carolina'),
(N'SD- South Dakota'),
(N'TN- Tennessee'),
(N'TX- Texas'),
(N'UT- Utah'),
(N'VT- Vermont'),
(N'VA- Virginia'),
(N'WA- Washington'),
(N'WV- West Virginia'),
(N'WI- Wisconsin'),
(N'WY- Wyoming'),
(N'AS- American Samoa'),
(N'GU- Guam'),
(N'MP- Northern Mariana Islands'),
(N'PR- Puerto Rico'),
(N'VI- Virgin Islands'),
(N'UM- U.S. Minor Outlying Islands'),
(N'FM- Federated States of Micronesia'),
(N'MH- Marshall Islands'),
(N'PW- Palau');
GO
CREATE TABLE [dbo].[codevaccinated] ([vaccinated] nvarchar(255) NOT NULL);
GO
INSERT INTO [dbo].[codevaccinated] ([vaccinated]) VALUES
(N'No'),
(N'Yes (Once)'),
(N'Yes (Twice)');
GO
CREATE TABLE [dbo].[codeyesno] ([YesNo] nvarchar(255) NOT NULL);
GO
INSERT INTO [dbo].[codeyesno] ([YesNo]) VALUES
(N'1- Yes'),
(N'2- No');
GO
