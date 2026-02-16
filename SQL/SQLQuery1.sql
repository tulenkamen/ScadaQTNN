CREATE TABLE Well_Alarm (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    WellId INT,
    ErrorCode INT,
    ErrorTime DATETIME,
    Description NVARCHAR(200)
)
