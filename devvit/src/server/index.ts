#!/usr/bin/env node

import { createServer, getServerPort } from '@devvit/web/server';
import { onRequest } from './server.ts';

const server = createServer(onRequest);
const port = getServerPort();

server.on('error', (error) => console.error('server-error', error));
server.listen(port, () => console.log(`Devvit backend listening on port ${port}`));
