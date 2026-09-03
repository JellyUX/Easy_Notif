import { defineConfig } from 'vitest/config';

// jsdom is needed for the pure/near-pure helpers in Web/kor-vote.js and Web/config.js that touch
// window.location / document. The plugin has no bundler and no other front-end test surface.
export default defineConfig({
    test: {
        environment: 'jsdom'
    }
});
