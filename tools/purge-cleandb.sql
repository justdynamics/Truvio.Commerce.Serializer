-- Purge Swift-CleanDB for a fresh Replace+Merge round-trip test (2026-04-20).
-- Iterates all ItemType_* tables via INFORMATION_SCHEMA then wipes core Content + Ecom.

-- Database is selected by the caller (sqlcmd -d) - a hardcoded USE here silently
-- redirected every purge to the legacy Swift-CleanDB regardless of the target (2026-06-11).
SET NOCOUNT ON;

BEGIN TRAN;
EXEC sp_MSforeachtable "ALTER TABLE ? NOCHECK CONSTRAINT ALL";

-- Dynamic DELETE on every ItemType_* table (Swift ItemType field-value storage)
DECLARE @sql NVARCHAR(MAX) = N'';
SELECT @sql = @sql + N'DELETE FROM [' + TABLE_NAME + N'];' + CHAR(10)
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME LIKE 'ItemType_%';
EXEC sp_executesql @sql;

-- Core content graph
DELETE FROM [Paragraph];
DELETE FROM [GridRow];
DELETE FROM [Page];
DELETE FROM [Area];
DELETE FROM [UrlPath];

-- Ecom relation tables (delete before parents)
DELETE FROM [EcomGroupProductRelation];
DELETE FROM [EcomVariantOptionsProductRelation];
DELETE FROM [EcomShopGroupRelation];
DELETE FROM [EcomShopLanguageRelation];
DELETE FROM [EcomMethodCountryRelation];
DELETE FROM [EcomVatCountryRelations];

-- Ecom core tables covered by the Replace+Merge config
DELETE FROM [EcomDiscountTranslation];
DELETE FROM [EcomDiscount];
DELETE FROM [EcomVariantsOptions];
DELETE FROM [EcomVariantGroups];
DELETE FROM [EcomProducts];
DELETE FROM [EcomGroups];
DELETE FROM [EcomOrderStateRules];
DELETE FROM [EcomOrderStates];
DELETE FROM [EcomOrderFlow];
DELETE FROM [EcomShippings];
DELETE FROM [EcomPayments];
DELETE FROM [EcomShops];
DELETE FROM [EcomVatGroups];
DELETE FROM [EcomLanguages];
DELETE FROM [EcomCurrencies];
DELETE FROM [EcomCountryText];
DELETE FROM [EcomCountries];

-- Reseed identity columns where present (ignore failures).
-- Only an identity that has been used (sys.identity_columns.last_value IS NOT NULL). On a table
-- that never held a row (a blank Dynamicweb database), RESEED 0 makes the FIRST insert get id 0,
-- not 1, and Dynamicweb rejects id 0 (Services.Pages.GetPage(0) throws). A never-used identity
-- needs no reseed: it hands out its seed.
DECLARE @reseedTable SYSNAME;
DECLARE reseedCursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT name FROM (VALUES
        ('Area'),
        ('Page'),
        ('Paragraph'),
        ('GridRow'),
        ('UrlPath'),
        ('EcomShops'),
        ('EcomPayments'),
        ('EcomShippings'),
        ('EcomVatGroups'),
        ('EcomOrderFlow'),
        ('EcomOrderStates')
    ) AS t(name);
OPEN reseedCursor;
FETCH NEXT FROM reseedCursor INTO @reseedTable;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF EXISTS (SELECT 1 FROM sys.identity_columns
               WHERE object_id = OBJECT_ID(@reseedTable) AND last_value IS NOT NULL)
    BEGIN
        BEGIN TRY DBCC CHECKIDENT (@reseedTable, RESEED, 0) WITH NO_INFOMSGS; END TRY BEGIN CATCH END CATCH
    END
    FETCH NEXT FROM reseedCursor INTO @reseedTable;
END
CLOSE reseedCursor;
DEALLOCATE reseedCursor;

COMMIT TRAN;
-- Re-enable FK with NOCHECK (WITHOUT verifying existing data) so the re-enable
-- doesn't abort on empty parent tables. Deserialize will repopulate referenced
-- rows; FKs remain structurally enforced for new writes after re-enable.
BEGIN TRY EXEC sp_MSforeachtable "ALTER TABLE ? CHECK CONSTRAINT ALL"; END TRY BEGIN CATCH END CATCH

SELECT 'Area' AS T, COUNT(*) AS N FROM [Area]
UNION ALL SELECT 'Page',          COUNT(*) FROM [Page]
UNION ALL SELECT 'Paragraph',     COUNT(*) FROM [Paragraph]
UNION ALL SELECT 'GridRow',       COUNT(*) FROM [GridRow]
UNION ALL SELECT 'EcomShops',     COUNT(*) FROM [EcomShops]
UNION ALL SELECT 'EcomCountries', COUNT(*) FROM [EcomCountries]
UNION ALL SELECT 'EcomProducts',  COUNT(*) FROM [EcomProducts]
UNION ALL SELECT 'UrlPath',       COUNT(*) FROM [UrlPath];

