SELECT
  CASE WHEN ConfigJson LIKE '%taxrates%' THEN 'taxrates'
       WHEN ConfigJson LIKE '%taxRates%' THEN 'taxRates'
       ELSE 'none' END AS KeyName,
  CASE WHEN ConfigJson LIKE '%"id":"A"%' THEN 'hasA' ELSE 'noA' END AS HasA,
  CASE WHEN ConfigJson LIKE '%"rate":16.500%' THEN 'r16.5'
       WHEN ConfigJson LIKE '%"rate":16.5%' THEN 'r16.5b'
       ELSE 'other' END AS RateMark
FROM dbo.Configurations
WHERE ConfigKey = 'mra.configuration.global';

SELECT
  JSON_VALUE(value, '$.id') AS RateId,
  JSON_VALUE(value, '$.rate') AS Rate,
  JSON_VALUE(value, '$.name') AS Name,
  JSON_VALUE(value, '$.chargeMode') AS Mode
FROM dbo.Configurations
CROSS APPLY OPENJSON(ConfigJson, '$.taxrates')
WHERE ConfigKey = 'mra.configuration.global';
