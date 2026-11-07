// Secure Enclave access for Vaultbar. CryptoKit has no C API, so .NET reaches it through this small library.
//
// CryptoKit is used because its key blob lives outside the keychain: the data-protection keychain needs an entitlement
// that an ad-hoc signed app cannot have, and the blob stays valid when an update re-signs the app.
import CryptoKit
import Foundation
import LocalAuthentication

private let ok: Int32 = 0
private let unexpected: Int32 = -1

private func status(of error: Error) -> Int32 {
    let code = (error as NSError).code
    return code == 0 ? unexpected : Int32(truncatingIfNeeded: code)
}

@_cdecl("vaultbar_touchid_available")
public func available() -> Int32 {
    var error: NSError?
    let canAsk = LAContext().canEvaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, error: &error)
    return SecureEnclave.isAvailable && canAsk ? 1 : 0
}

/// Creates a key only the Secure Enclave can use, and only after Touch ID for the fingers enrolled right now.
@_cdecl("vaultbar_touchid_create")
public func create(
    _ blob: UnsafeMutablePointer<UInt8>, _ capacity: Int32, _ written: UnsafeMutablePointer<Int32>
) -> Int32 {
    var failure: Unmanaged<CFError>?
    guard let access = SecAccessControlCreateWithFlags(
        nil, kSecAttrAccessibleWhenUnlockedThisDeviceOnly, [.privateKeyUsage, .biometryCurrentSet], &failure)
    else { return failure.map { status(of: $0.takeRetainedValue()) } ?? unexpected }
    do {
        let key = try SecureEnclave.P256.KeyAgreement.PrivateKey(accessControl: access)
        let data = key.dataRepresentation
        guard data.count <= Int(capacity) else { return unexpected }
        data.copyBytes(to: blob, count: data.count)
        written.pointee = Int32(data.count)
        return ok
    } catch {
        return status(of: error)
    }
}

/// Turns the challenge into a P-256 public key and agrees on a secret with the enclave key, which asks for Touch ID.
/// The same key and challenge always give the same 32 bytes, and nothing outside the enclave can compute them.
@_cdecl("vaultbar_touchid_derive")
public func derive(
    _ blob: UnsafePointer<UInt8>, _ blobLength: Int32,
    _ challenge: UnsafePointer<UInt8>, _ challengeLength: Int32,
    _ reason: UnsafePointer<CChar>, _ noPrompt: Int32,
    _ secret: UnsafeMutablePointer<UInt8>
) -> Int32 {
    do {
        let context = LAContext()
        context.localizedReason = String(cString: reason)
        context.interactionNotAllowed = noPrompt != 0
        let key = try SecureEnclave.P256.KeyAgreement.PrivateKey(
            dataRepresentation: Data(bytes: blob, count: Int(blobLength)), authenticationContext: context)
        let scalar = SHA256.hash(data: Data(bytes: challenge, count: Int(challengeLength)))
        let peer = try P256.KeyAgreement.PrivateKey(rawRepresentation: Data(scalar))
        let shared = try key.sharedSecretFromKeyAgreement(with: peer.publicKey)
        shared.withUnsafeBytes { secret.update(from: $0.bindMemory(to: UInt8.self).baseAddress!, count: 32) }
        return ok
    } catch {
        return status(of: error)
    }
}
