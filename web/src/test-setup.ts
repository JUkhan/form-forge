import { expect } from 'vitest'
import * as matchers from '@testing-library/jest-dom/matchers'

// Explicit extend (vs side-effect import of '@testing-library/jest-dom')
// because we don't enable vitest globals — the side-effect import needs a
// global `expect` to attach matchers to.
expect.extend(matchers)

// Type augmentation: '@testing-library/jest-dom/matchers' ships the runtime
// matchers but NOT the `declare module 'vitest'` augmentation, so tsc doesn't
// see toBeInTheDocument() / toHaveTextContent() / etc. on Assertion<T>. The
// /vitest entrypoint exists exactly for this — side-effect import; it doesn't
// re-register matchers (we already did that above).
import '@testing-library/jest-dom/vitest'
