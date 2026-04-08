INSERT INTO Locations_new (Id, Name, Latitude, Longitude, Weather, Temperature, LastUpdated)
SELECT
    Id,
    Name,
    Latitude,
    Longitude,
    COALESCE(Weather, 'N/A'),
    COALESCE(Temperature, 0),
    COALESCE(LastUpdated, '0001-01-01T00:00:00.0000000')
FROM Locations;

