CREATE TABLE Locations_new (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    Latitude REAL NOT NULL,
    Longitude REAL NOT NULL,
    Weather TEXT NOT NULL,
    Temperature REAL NOT NULL DEFAULT 0,
    LastUpdated TEXT NOT NULL
);

