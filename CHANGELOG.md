# Changelog

## [1.5.1.2] - 2026-02-06
### Fixed
- Fixed typo in folder name (`SheduledTasks` → `ScheduledTasks`)
- Fixed missing `await` in `SaveItemAsync`
- Restored stable season mapping logic (reverted potentially unstable FlareSolverr scraping changes)

## [1.5.1.1] - 2026-02-05
### Added
- Episode maturity ratings support
- Minimum score threshold (70%) for series matching

### Fixed
- Season matching specific fix using SeasonSequenceNumber
- Episodes now preserve Jellyfin's IndexNumber for compatibility
