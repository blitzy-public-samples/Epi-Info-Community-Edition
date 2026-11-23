-- Post-load checks. Each row should show ok = 1.
USE [EpiRehearsal];
SET NOCOUNT ON;
SELECT 'metaViews' AS check_name, COUNT(*) AS actual, 2 AS expected, CASE WHEN COUNT(*) = 2 THEN 1 ELSE 0 END AS ok FROM metaViews
UNION ALL SELECT 'metaPages', COUNT(*), 3, CASE WHEN COUNT(*) = 3 THEN 1 ELSE 0 END FROM metaPages
UNION ALL SELECT 'metaFieldTypes', COUNT(*), 28, CASE WHEN COUNT(*) = 28 THEN 1 ELSE 0 END FROM metaFieldTypes
UNION ALL SELECT 'CaseReport records', COUNT(*), 1409, CASE WHEN COUNT(*) = 1409 THEN 1 ELSE 0 END FROM CaseReport
UNION ALL SELECT 'FoodHistory records', COUNT(*), 609, CASE WHEN COUNT(*) = 609 THEN 1 ELSE 0 END FROM FoodHistory
UNION ALL SELECT 'CaseReport1 orphans', COUNT(*), 0, CASE WHEN COUNT(*) = 0 THEN 1 ELSE 0 END FROM CaseReport b LEFT JOIN CaseReport1 p ON p.GlobalRecordId = b.GlobalRecordId WHERE p.GlobalRecordId IS NULL
UNION ALL SELECT 'FoodHistory2 orphans', COUNT(*), 0, CASE WHEN COUNT(*) = 0 THEN 1 ELSE 0 END FROM FoodHistory b LEFT JOIN FoodHistory2 p ON p.GlobalRecordId = b.GlobalRecordId WHERE p.GlobalRecordId IS NULL
UNION ALL SELECT 'FoodHistory3 orphans', COUNT(*), 0, CASE WHEN COUNT(*) = 0 THEN 1 ELSE 0 END FROM FoodHistory b LEFT JOIN FoodHistory3 p ON p.GlobalRecordId = b.GlobalRecordId WHERE p.GlobalRecordId IS NULL
UNION ALL SELECT 'soft-deleted records', COUNT(*), 29, CASE WHEN COUNT(*) = 29 THEN 1 ELSE 0 END FROM (SELECT RecStatus FROM CaseReport UNION ALL SELECT RecStatus FROM FoodHistory) x WHERE RecStatus = 0;
GO
