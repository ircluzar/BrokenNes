This folder contains 6502 span predictor models for the Imagine feature and JSON database files.

- 6502_span_predictor_epoch*.onnx — ONNX models for AI-powered corruption
- models.json — manifest describing available epochs and default selection
- meta_games.json — RetroAchievements metadata for games
- default-db.json — default database configuration

These files are copied to the output directory on build and loaded by the WinForms application.
