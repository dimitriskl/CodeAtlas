import { describe, expect, it } from "vitest";
import { appVersion, contractVersion, schemaVersion } from "../src/index.js";

describe("version constants", () => {
  it("keeps the contract, schema, and application versions separate", () => {
    // Arrange
    const expectedAppVersion = "0.00.01";

    // Act
    const reported = { appVersion, contractVersion, schemaVersion };

    // Assert
    expect(reported.appVersion).toBe(expectedAppVersion);
    expect(reported.contractVersion).toBe(1);
    expect(reported.schemaVersion).toBe(2);
    expect(reported.appVersion).not.toBe(String(reported.contractVersion));
    expect(reported.appVersion).not.toBe(String(reported.schemaVersion));
  });
});
