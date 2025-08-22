SELECT
    "Symbol",
    "IsActive",
    "DateAdded",
    "LastScanned"
FROM
    public."TrackedSymbols"
ORDER BY
    "IsActive" DESC, "Symbol" ASC;