# BrokenNes Web API

This directory contains the Web API server for BrokenNes webmodules.

## Overview

The Web API server provides HTTP endpoints for webmodules to interact with the emulator. It listens only on `localhost:42067` to avoid requiring admin privileges.

## Server Details

- **Port**: 42067
- **Address**: 127.0.0.1 (loopback only)
- **Protocol**: HTTP
- **Framework**: ASP.NET Core Minimal API

## Implemented Endpoints

### Memory Access

All memory access endpoints are implemented according to the webmodule-api-requirements.md specification.

#### Health Check
- `GET /api/health` - Check if API server is running

#### Memory Domains
- `GET /api/memory/domains` - Get list of available memory domains
- `GET /api/memory/domain/{domainName}/size` - Get size of specific domain

#### Peek/Poke Operations
- `GET /api/memory/peek?domain={domain}&address={address}` - Read single byte
- `POST /api/memory/poke` - Write single byte
  - Body: `{ "Domain": "string", "Address": int, "Value": byte }`
- `GET /api/memory/peek-range?domain={domain}&address={address}&length={length}` - Read multiple bytes
- `POST /api/memory/poke-range` - Write multiple bytes
  - Body: `{ "Domain": "string", "Address": int, "Data": byte[] }`

## Available Memory Domains

- **System RAM** - 2KB NES system RAM (mirrored at $0000-$07FF)
- **CPU Bus** - Full 64KB CPU address space
- **PRG ROM** - Cartridge PRG ROM data
- **PRG RAM** - Cartridge PRG RAM/SRAM
- **CHR** - Cartridge CHR ROM/RAM

## Testing

A complete test suite is available at:
```
Windows/Webmodules/ApiTest/index.html
```

The ApiTest webmodule provides:
- Interactive testing of all Memory Access endpoints
- Hex editor-style memory viewer
- Automated test runner
- Example API calls with results

## Usage Example

```javascript
// Health check
const response = await fetch('http://127.0.0.1:42067/api/health');
const data = await response.json();

// Peek memory
const peek = await fetch('http://127.0.0.1:42067/api/memory/peek?domain=System%20RAM&address=0');
const peekData = await peek.json();

// Poke memory
const poke = await fetch('http://127.0.0.1:42067/api/memory/poke', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ Domain: 'System RAM', Address: 0, Value: 255 })
});
```

## Architecture

The Web API server is implemented as:
- `WebApiServer.cs` - Main server class with ASP.NET Core setup
- `NesMemoryExtensions.cs` - Extension methods for NES memory access

The server is automatically started when BrokenNes launches and stopped when the application closes.

## Security

The server only listens on the loopback interface (127.0.0.1), meaning:
- Only the local computer can access it
- No admin privileges are required
- No firewall configuration needed
- External network access is impossible

## Future Endpoints

See `docs/webmodule-api-requirements.md` for the full list of planned endpoints including:
- CPU State Access
- PPU State Access
- APU State Access
- Real-Time Corruptor (RTC)
- Glitch Harvester (GH)
- Imagine (AI-Powered Corruption)
- Achievements
- Emulation Control
- ROM Management
- State Persistence
- Display Settings
- Core Selection
