/** JSON envelope contract. Independent of the index schema and the application version. */
export const contractVersion = 0;

/** Persisted index schema. Independent of the JSON contract and the application version. */
export const schemaVersion = 0;

/** Application version (`x.xx.xx`). Stub value so the seam test fails until wired to the package. */
export const appVersion = "0.0.0";
