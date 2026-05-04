using UnityEngine;

namespace FuckRipper.AvatarObfuscator
{
    /// <summary>
    /// Drop this on the avatar root (the GameObject that has VRCAvatarDescriptor).
    /// At upload / Play time, NDMF clones the avatar and our plugin obfuscates the clone.
    /// The original avatar in the scene is never modified, so removing this component
    /// fully reverts the change — there is nothing to revert.
    ///
    /// <para>Project: <see href="https://github.com/cocokoishi/AvatarObfuscator"/></para>
    /// <para>Author: <see href="https://space.bilibili.com/5145514"/></para>
    /// </summary>
    [AddComponentMenu("Cocokoishi/Avatar Obfuscator")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/cocokoishi/AvatarObfuscator")]
    public sealed class AvatarObfuscator : MonoBehaviour
#if FR_OBF_VRCSDK3_AVATARS
        , VRC.SDKBase.IEditorOnly
#endif
    {
        // ---------------------------------------------------------------
        // Project links — kept here as constants so the inspector can show
        // them at the bottom of the component without anyone having to
        // remember the URLs.
        //
        // GitHub : https://github.com/cocokoishi/AvatarObfuscator
        // Bilibili: https://space.bilibili.com/5145514
        // ---------------------------------------------------------------
        public const string ProjectUrl = "https://github.com/cocokoishi/AvatarObfuscator";
        public const string AuthorUrl  = "https://space.bilibili.com/5145514";

        [SerializeField]
        public ObfuscationOptions options = new ObfuscationOptions();
    }
}
