# sharpclaw-web

Simple React UI for the SharpClaw agent API.

## Features

- list sessions for an agent
- create a new session
- load full session history
- send messages to a session
- stream run updates over SSE while the agent responds

## Setup

1. Install dependencies:
   - `npm install`
2. Configure API URL:
   - copy `.env.example` to `.env`
   - set `VITE_API_BASE_URL` to your API base address
3. Run the app:
   - `npm run dev`

Default UI URL: `http://localhost:5173`

## Required API endpoints

- `POST /sessions`
- `GET /agents/{agentId}/sessions`
- `GET /sessions/{sessionId}/history`
- `POST /sessions/{sessionId}/messages`
- `GET /sessions/{sessionId}/runs/{runId}/stream`

## Notes

- If the API uses HTTPS with a development certificate, trust the certificate in your browser.
- The current backend session storage is in-memory; restarting the API clears sessions.
