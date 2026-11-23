-- Drops and recreates the rehearsal database. Safe to rerun; it resets all test data.
USE master;
GO
IF DB_ID(N'EpiRehearsal') IS NOT NULL BEGIN ALTER DATABASE [EpiRehearsal] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [EpiRehearsal]; END
GO
CREATE DATABASE [EpiRehearsal];
GO
