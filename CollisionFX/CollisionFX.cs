﻿using System;
using UnityEngine;
using ModuleWheels;

/**********************************************************
 * TODO:
 * Add support for 0 - 255 range colours rather than 0 - 1.
 * OnCollisionStay stops sounds for all attached parts. These parts may be in contact too.
 * Fix new ClosestPointOnBounds code.
 * Add dust for wheels.
 * Different dust movement in different gravities.
 * Selectable particle effects based on body.
 * Variable dust duration by biome: Dry areas should be more dusty. Dustiness float setting?
 * Vacuum collision particles with optional sound disable flag.
 * Dust wind input.
 * 
 * Possible features:
 * Wheel skid sounds and smoke: WheelHit.(forward/side)Slip
 * Investigate using particle stretching to fake vacuum \
 * body dust jets for engines.
 **********************************************************/

namespace CollisionFX
{
    public class CollisionFX : PartModule
    {
        public static string ConfigPath = "GameData/CollisionFX/settings.cfg";

        [KSPField]
        public float volume = 0.5f;
        [KSPField]
        public bool scrapeSparks = true;
        [KSPField]
        public string collisionSound = String.Empty;
        [KSPField]
        public string wheelImpactSound = String.Empty;
        [KSPField]
        public string scrapeSound = String.Empty;
        [KSPField]
        public string sparkSound = String.Empty;
        [KSPField]
        public float sparkLightIntensity = 0.05f;
        [KSPField]
        public float minScrapeSpeed = 1f;

        public float pitchRange = 0.3f;
        public float scrapeFadeSpeed = 5f;

        private GameObject sparkFx;
        private ParticleEmitter sparkFxParticleEmitter;

        private GameObject dustFx;
        private ParticleEmitter dustFxParticleEmitter;
        ParticleAnimator dustAnimator;
        //private GameObject fragmentFx;
        //ParticleAnimator fragmentAnimator;
        private ModuleWheelBase moduleWheel = null;
        private ModuleWheelDamage moduleWheelDamage = null;
        private ModuleWheelDeployment moduleWheelDeployment = null;
        private WheelCollider wheelCollider = null;
        private FXGroup ScrapeSounds = new FXGroup("ScrapeSounds");
        private FXGroup SparkSounds = new FXGroup("SparkSounds");
        private FXGroup BangSound = new FXGroup("BangSound");
        private FXGroup WheelImpactSound = null;
        private Light scrapeLight;
        private Color lightColor1 = new Color(254, 226, 160); // Tan / light orange
        private Color lightColor2 = new Color(239, 117, 5); // Red-orange.

#if DEBUG
        private GameObject[] spheres = new GameObject[4];
        private bool useSpheres = false;
#endif

        public class CollisionInfo
        {
            public CollisionFX CollisionFX;
            public bool IsWheel;

            public CollisionInfo(CollisionFX collisionFX, bool isWheel)
            {
                CollisionFX = collisionFX;
                IsWheel = isWheel;
            }
        }

        #region Events

        public override void OnStart(StartState state)
        {
            if (state == StartState.Editor || state == StartState.None) return;

            SetupParticles();
            if (scrapeSparks)
                SetupLight();

            moduleWheel = part.FindModuleImplementing<ModuleWheelBase>();
            if (moduleWheel != null)
            {
                moduleWheelDamage = part.FindModuleImplementing<ModuleWheelDamage>();
                moduleWheelDeployment = part.FindModuleImplementing<ModuleWheelDeployment>();
                wheelCollider = moduleWheel.wheelColliderHost.GetComponent<WheelCollider>();
            }

            SetupAudio();

            GameEvents.onGamePause.Add(OnPause);
            GameEvents.onGameUnpause.Add(OnUnpause);
            
#if DEBUG
            if (useSpheres)
            {
                for (int i = 0; i < spheres.Length; i++)
                {
                    spheres[i] = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Destroy(spheres[i].GetComponent<Collider>());
                }
                spheres[0].GetComponent<Renderer>().material.color = Color.red;
                spheres[1].GetComponent<Renderer>().material.color = Color.green;
                spheres[3].GetComponent<Renderer>().material.color = Color.yellow;
            }
#endif
        }

        private bool _paused = false;
        private void OnPause()
        {
            _paused = true;
            if (SparkSounds != null && SparkSounds.audio != null)
                SparkSounds.audio.Stop();
            if (ScrapeSounds != null && ScrapeSounds.audio != null)
                ScrapeSounds.audio.Stop();
        }

        private void OnUnpause()
        {
            _paused = false;
        }

        private void OnDestroy()
        {
            if (ScrapeSounds != null && ScrapeSounds.audio != null)
                ScrapeSounds.audio.Stop();
            if (SparkSounds != null && SparkSounds.audio != null)
                SparkSounds.audio.Stop();
            GameEvents.onGamePause.Remove(OnPause);
            GameEvents.onGameUnpause.Remove(OnUnpause);
            _paused = true;
        }

        // Not called on parts where physicalSignificance = false. Check the parent part instead.
        public void OnCollisionEnter(Collision c)
        {
            if (_paused) return;
            if (c.relativeVelocity.magnitude > 3)
            {
                if (c.contacts.Length == 0)
                    return;

                foreach (ContactPoint contactPoint in c.contacts)
                {
                    Part collidingPart = GetCollidingPart(contactPoint);
                    if (collidingPart != null)
                    {
                        CollisionFX cfx = collidingPart.GetComponent<CollisionFX>();
                        if (cfx != null)
                        {
                            DustImpact(c.relativeVelocity.magnitude, contactPoint.point, c.collider.name);

                            bool isUsableWheel = true;
                            if (cfx.moduleWheel != null)
                            {
                                if (cfx.moduleWheelDamage != null && cfx.moduleWheelDamage.isDamaged)
                                {
                                    isUsableWheel = false;
                                }
                                if (cfx.moduleWheelDeployment != null && cfx.moduleWheelDeployment.Position == 0)
                                {
                                    isUsableWheel = false;
                                }
                            }
                            else
                                isUsableWheel = false;

                            cfx.ImpactSounds(isUsableWheel);
                        }
                    }
                }
            }
        }

        // Not called on parts where physicalSignificance = false. Check the parent part instead.
        public void OnCollisionStay(Collision c)
        {
            if (!scrapeSparks || _paused) return;

#if DEBUG
            if (useSpheres)
            {
                spheres[3].GetComponent<Renderer>().enabled = true;
                spheres[3].transform.position = c.contacts[0].point;
            }
#endif
            foreach (ContactPoint contactPoint in c.contacts)
            {
                // Contact points are from the previous frame. Add the velocity to get the correct position.
                // Only the parent part has a rigidbody, so use it for all adjustments.
                Vector3 point = Utils.PointToCurrentFrame(contactPoint.point, part);

                Part foundPart = GetCollidingPart(contactPoint);
                if (foundPart != null)
                {
                    CollisionFX cfx = foundPart.GetComponent<CollisionFX>();
                    if (cfx != null)
                    {
                        cfx.Scrape(foundPart, c.gameObject, c.transform, point, c.relativeVelocity.magnitude, c.collider.name, (contactPoint.thisCollider is WheelCollider));
                    }
                }
            }
        }

        private void OnCollisionExit(Collision c)
        {
#if DEBUG
            if (useSpheres)
                spheres[3].GetComponent<Renderer>().enabled = false;
#endif

            StopScrapeLightSound();
            foreach (ContactPoint contactPoint in c.contacts)
            {
                Part collidingPart = GetCollidingPart(contactPoint);
                if (collidingPart != null)
                {
                    CollisionFX cfx = collidingPart.GetComponent<CollisionFX>();
                    if (cfx != null)
                        cfx.StopScrapeLightSound();
                }
            }
        }

        #endregion Events

        private Part GetCollidingPart(ContactPoint contactPoint)
        {
            GameObject searchObject = contactPoint.thisCollider.gameObject;
            Part foundPart = null;
            bool searchedOtherCollider = false;
            while (searchObject != null)
            {
                foundPart = searchObject.GetComponent<Part>();
                if (foundPart != null)
                    return foundPart;
                searchObject = searchObject.transform == null ? null :
                    searchObject.transform.parent == null ? null :
                    searchObject.transform.parent.gameObject;
                if (searchObject == null && !searchedOtherCollider) // "thisCollider" is sometimes the ground...
                {
                    searchedOtherCollider = true;
                    searchObject = contactPoint.otherCollider.gameObject;
                }
            }
            return null;
        }

        /// <summary>
        /// This part has come into contact with something. Play an appropriate sound.
        /// </summary>
        public void ImpactSounds(bool isWheel)
        {
            if (isWheel && WheelImpactSound != null && WheelImpactSound.audio != null)
            {
                WheelImpactSound.audio.pitch = UnityEngine.Random.Range(1 - pitchRange, 1 + pitchRange);
                WheelImpactSound.audio.Play();
                if (BangSound != null && BangSound.audio != null)
                {
                    BangSound.audio.Stop();
                }
            }
            else
            {
                if (BangSound != null && BangSound.audio != null)
                {
                    // Shift the pitch randomly each time so that the impacts don't all sound the same.
                    BangSound.audio.pitch = UnityEngine.Random.Range(1 - pitchRange, 1 + pitchRange);
                    BangSound.audio.Play();
                }
                if (WheelImpactSound != null && WheelImpactSound.audio != null)
                {
                    WheelImpactSound.audio.Stop();
                }
            }
        }

        /*private string[] particleTypes = {
                                            "fx_exhaustFlame_white_tiny",
                                            "fx_exhaustFlame_yellow",
                                            "fx_exhaustFlame_blue",
                                            //"fx_exhaustLight_yellow",
                                            "fx_exhaustLight_blue",
                                            "fx_exhaustFlame_blue_small",
                                            "fx_smokeTrail_light",
                                            "fx_smokeTrail_medium",
                                            "fx_smokeTrail_large",
                                            "fx_smokeTrail_veryLarge",
                                            "fx_smokeTrail_aeroSpike",
                                            "fx_gasBurst_white",
                                            "fx_gasJet_white",
                                            "fx_SRB_large_emit",
                                            "fx_SRB_large_emit2",
                                            "fx_exhaustSparks_flameout",
                                            "fx_exhaustSparks_flameout_2",
                                            "fx_exhaustSparks_yellow",
                                            "fx_shockExhaust_red_small", nope
                                            "fx_shockExhaust_blue_small",
                                            "fx_shockExhaust_blue",
                                            "fx_LES_emit",
                                            "fx_ksX_emit",
                                            "fx_ks25_emit",
                                            "fx_ks1_emit"
                                         };*/

        //int currentParticle = 0;
        private void SetupParticles()
        {
            /*UnityEngine.Object o = null;
            while (o == null)
            {
                string name = "Effects/" + particleTypes[currentParticle];
                o = UnityEngine.Resources.Load(name);
                currentParticle++;
                if (currentParticle >= particleTypes.Length) currentParticle = 0;
            }*/

            //ScreenMessages.PostScreenMessage(particleTypes[currentParticle]);
            if (scrapeSparks)
            {
                sparkFx = (GameObject)GameObject.Instantiate(UnityEngine.Resources.Load("Effects/fx_exhaustSparks_flameout"));
                sparkFxParticleEmitter = sparkFx.GetComponent<ParticleEmitter>();
                sparkFx.transform.parent = part.transform;
                sparkFx.transform.position = part.transform.position;
                sparkFxParticleEmitter.localVelocity = Vector3.zero;
                sparkFxParticleEmitter.useWorldSpace = true;
                sparkFxParticleEmitter.emit = false;
                sparkFxParticleEmitter.minEnergy = 0;
                sparkFxParticleEmitter.minEmission = 0;
            }

            dustFx = (GameObject)GameObject.Instantiate(UnityEngine.Resources.Load("Effects/fx_smokeTrail_light"));
            dustFxParticleEmitter = dustFx.GetComponent<ParticleEmitter>();
            dustFx.transform.parent = part.transform;
            dustFx.transform.position = part.transform.position;
            dustFxParticleEmitter.localVelocity = Vector3.zero;
            dustFxParticleEmitter.useWorldSpace = true;
            dustFxParticleEmitter.emit = false;
            dustFxParticleEmitter.minEnergy = 0;
            dustFxParticleEmitter.minEmission = 0;
            dustAnimator = dustFxParticleEmitter.GetComponent<ParticleAnimator>();

            /*fragmentFx = (GameObject)GameObject.Instantiate(UnityEngine.Resources.Load("Effects/fx_exhaustSparks_yellow"));
            fragmentFx.transform.parent = part.transform;
            fragmentFx.transform.position = part.transform.position;
            fragmentFx.particleEmitter.localVelocity = Vector3.zero;
            fragmentFx.particleEmitter.useWorldSpace = true;
            fragmentFx.particleEmitter.emit = false;
            fragmentFx.particleEmitter.minEnergy = 0;
            fragmentFx.particleEmitter.minEmission = 0;
            fragmentAnimator = fragmentFx.particleEmitter.GetComponent<ParticleAnimator>();*/
        }

        private void SetupLight()
        {
            scrapeLight = sparkFx.AddComponent<Light>();
            scrapeLight.type = LightType.Point;
            scrapeLight.range = 3f;
            scrapeLight.shadows = LightShadows.None;
            scrapeLight.enabled = false;
        }

        private void SetupAudio()
        {
            if (scrapeSparks)
            {
                if (SparkSounds == null)
                {
                    Debug.LogError("[CollisionFX] SparkSounds was null");
                    return;
                }
                if (!String.IsNullOrEmpty(sparkSound))
                {
                    part.fxGroups.Add(SparkSounds);
                    SparkSounds.name = "SparkSounds";
                    SparkSounds.audio = gameObject.AddComponent<AudioSource>();
                    SparkSounds.audio.clip = GameDatabase.Instance.GetAudioClip(sparkSound);
                    if (SparkSounds.audio.clip == null)
                    {
                        Debug.LogError("[CollisionFX] Unable to load sparkSound \"" + sparkSound + "\"");
                        scrapeSparks = false;
                        return;
                    }
                    SparkSounds.audio.dopplerLevel = 0f;
                    SparkSounds.audio.rolloffMode = AudioRolloffMode.Logarithmic;
                    SparkSounds.audio.Stop();
                    SparkSounds.audio.loop = true;
                    SparkSounds.audio.volume = volume * GameSettings.SHIP_VOLUME;
                    SparkSounds.audio.time = UnityEngine.Random.Range(0, SparkSounds.audio.clip.length);
                }
            }

            if (ScrapeSounds == null)
            {
                Debug.LogError("[CollisionFX] ScrapeSounds was null");
                return;
            }
            if (!String.IsNullOrEmpty(scrapeSound))
            {
                part.fxGroups.Add(ScrapeSounds);
                ScrapeSounds.name = "ScrapeSounds";
                ScrapeSounds.audio = gameObject.AddComponent<AudioSource>();
                ScrapeSounds.audio.clip = GameDatabase.Instance.GetAudioClip(scrapeSound);
                if (ScrapeSounds.audio.clip == null)
                {
                    Debug.LogError("[CollisionFX] Unable to load scrapeSound \"" + scrapeSound + "\"");
                }
                else
                {
                    ScrapeSounds.audio.dopplerLevel = 0f;
                    ScrapeSounds.audio.rolloffMode = AudioRolloffMode.Logarithmic;
                    ScrapeSounds.audio.Stop();
                    ScrapeSounds.audio.loop = true;
                    ScrapeSounds.audio.volume = volume * GameSettings.SHIP_VOLUME;
                    ScrapeSounds.audio.time = UnityEngine.Random.Range(0, ScrapeSounds.audio.clip.length);
                }
            }

            if (!String.IsNullOrEmpty(collisionSound))
            {
                part.fxGroups.Add(BangSound);
                BangSound.name = "BangSound";
                BangSound.audio = gameObject.AddComponent<AudioSource>();
                BangSound.audio.clip = GameDatabase.Instance.GetAudioClip(collisionSound);
                BangSound.audio.dopplerLevel = 0f;
                BangSound.audio.rolloffMode = AudioRolloffMode.Logarithmic;
                BangSound.audio.Stop();
                BangSound.audio.loop = false;
                BangSound.audio.volume = GameSettings.SHIP_VOLUME;
            }

            if (wheelCollider != null && !String.IsNullOrEmpty(wheelImpactSound))
            {
                WheelImpactSound = new FXGroup("WheelImpactSound");
                part.fxGroups.Add(WheelImpactSound);
                WheelImpactSound.name = "WheelImpactSound";
                WheelImpactSound.audio = gameObject.AddComponent<AudioSource>();
                WheelImpactSound.audio.clip = GameDatabase.Instance.GetAudioClip(wheelImpactSound);
                WheelImpactSound.audio.dopplerLevel = 0f;
                WheelImpactSound.audio.rolloffMode = AudioRolloffMode.Logarithmic;
                WheelImpactSound.audio.Stop();
                WheelImpactSound.audio.loop = false;
                WheelImpactSound.audio.volume = GameSettings.SHIP_VOLUME;
            }
        }

        public void DebugParticles(string colliderName, Vector3 contactPoint)
        {
            Color c = ColourManager.GetDustColour(colliderName);
            dustFx.transform.position = contactPoint;
            dustFx.GetComponent<ParticleEmitter>().maxEnergy = 10;
            dustFx.GetComponent<ParticleEmitter>().maxEmission = 75;
            dustFx.GetComponent<ParticleEmitter>().Emit();
            //dustFx.particleEmitter.worldVelocity = -part.Rigidbody.velocity;
            // Set dust biome colour.
            if (dustAnimator != null)
            {
                Color[] colors = dustAnimator.colorAnimation;
                colors[0] = c;
                colors[1] = c;
                colors[2] = c;
                colors[3] = c;
                colors[4] = c;
                dustAnimator.colorAnimation = colors;
            }
        }

        public void Update()
        {
            bool x = false;
        }

        /// <summary>
        /// Checks whether the collision is happening on this part's wheel.
        /// </summary>
        /// <returns></returns>
        private bool IsCollidingWheel(Vector3 collisionPoint)
        {
            if (wheelCollider == null) return false;
            float wheelDistance = Vector3.Distance(wheelCollider.ClosestPointOnBounds(collisionPoint), collisionPoint);
            float partDistance = Vector3.Distance(part.collider.ClosestPointOnBounds(collisionPoint), collisionPoint);
            return wheelDistance < partDistance;
        }

        /// <summary>
        /// This part is moving against another surface. Create sound, light and particle effects.
        /// </summary>
        /// <param name="part"></param>
        /// <param name="collidedWith"></param>
        /// <param name="collidedWithTransform"></param>
        /// <param name="position"></param>
        /// <param name="relativeVelocity"></param>
        /// <param name="colliderName"></param>
        /// <param name="tyreCollision"></param>
        public void Scrape(Part part, GameObject collidedWith, Transform collidedWithTransform, Vector3 position, 
            float relativeVelocity, string colliderName, bool tyreCollision)
        {
            if (_paused || part == null)
            {
                StopScrapeLightSound();
                return;
            }

            // TODO: Use tyreCollision for something (if it works).

            // Wheels lose their wheel collider as soon as retraction starts.
            if (moduleWheel != null)
            { // Has a wheel module.
                bool wheelIntact = moduleWheelDamage == null || !moduleWheelDamage.isDamaged;
                bool wheelDeployed = moduleWheelDeployment == null || moduleWheelDeployment.Position > 0;

                if (wheelIntact && wheelDeployed)
                {
                    StopScrapeLightSound();
                    return;
                }
            }

            if (sparkFx != null)
                sparkFx.transform.LookAt(collidedWithTransform);

            ScrapeParticles(relativeVelocity, position, colliderName, collidedWith);
            ScrapeSound(ScrapeSounds, relativeVelocity);

            if (CanSpark(colliderName, collidedWith))
                ScrapeSound(SparkSounds, relativeVelocity);
            else
            {
                if (SparkSounds != null && SparkSounds.audio != null)
                    SparkSounds.audio.Stop();
            }

#if DEBUG
            if (useSpheres)
            {
                spheres[0].GetComponent<Renderer>().enabled = false;
                spheres[1].GetComponent<Renderer>().enabled = true;
                spheres[1].transform.position = part.transform.position;
                spheres[2].transform.position = position;
            }
#endif
        }

        public void StopScrapeLightSound()
        {
            if (scrapeSparks)
            {
                if (SparkSounds != null && SparkSounds.audio != null)
                    SparkSounds.audio.Stop();
#if DEBUG
                if (useSpheres)
                {
                    spheres[0].transform.position = part.transform.position;
                    spheres[0].GetComponent<Renderer>().enabled = true;
                    spheres[1].GetComponent<Renderer>().enabled = false;
                }
#endif
                scrapeLight.enabled = false;
            }

            if (ScrapeSounds != null && ScrapeSounds.audio != null)
            {
                ScrapeSounds.audio.Stop();
            }
        }

        /* Kerbin natural biomes:
            Water           Sand. If we're hitting a terrain collider here, it will be sandy.
            Grasslands      Dirt
            Highlands       Dirt (dark?)
            Shores          Dirt (too grassy for sand)
            Mountains       Rock particles?
            Deserts         Sand
            Badlands        ?
            Tundra          Dirt?
            Ice Caps        Snow
         */

        public bool HasUsableWheel()
        {
            if (wheelCollider == null)
                return false;
            // Has a wheel collider.

            if (moduleWheel == null)
                return false;
            // Has a wheel module.

            if (moduleWheelDamage != null && moduleWheelDamage.isDamaged)
                return false;
            // Has an intact wheel.

            return (moduleWheelDeployment == null || moduleWheelDeployment.Position > 0);
        }

        /// <summary>
        /// Whether the object collided with produces sparks.
        /// </summary>
        /// <returns>True if the object is a CollisionFX with scrapeSparks or a non-CollisionFX object.</returns>
        public bool TargetScrapeSparks(GameObject collidedWith)
        {
            CollisionFX objectCollidedWith = collidedWith.GetComponent<CollisionFX>();
            return objectCollidedWith == null ? true : objectCollidedWith.scrapeSparks;
        }

        public bool CanSpark(string colliderName, GameObject collidedWith)
        {
            return scrapeSparks && TargetScrapeSparks(collidedWith) && FlightGlobals.ActiveVessel.atmDensity > 0 &&
                FlightGlobals.currentMainBody.atmosphereContainsOxygen && !Utils.IsPQS(colliderName);
        }

        private void ScrapeParticles(float speed, Vector3 contactPoint, string colliderName, GameObject collidedWith)
        {
            if (speed > minScrapeSpeed)
            {
                if (!Utils.IsPQS(colliderName) && TargetScrapeSparks(collidedWith) && !Utils.IsRagdoll(collidedWith))
                {
                    if (CanSpark(colliderName, collidedWith) && FlightGlobals.ActiveVessel.atmDensity > 0 && FlightGlobals.currentMainBody.atmosphereContainsOxygen)
                    {
                        sparkFx.transform.position = contactPoint;
                        sparkFxParticleEmitter.maxEnergy = speed / 10;                          // Values determined
                        sparkFxParticleEmitter.maxEmission = Mathf.Clamp((speed * 2), 0, 75);   // via experimentation.
                        sparkFxParticleEmitter.Emit();
                        sparkFxParticleEmitter.worldVelocity = -part.Rigidbody.velocity;
                        scrapeLight.enabled = true;
                        scrapeLight.color = Color.Lerp(lightColor1, lightColor2, UnityEngine.Random.Range(0f, 1f));
                        float intensityMultiplier = 1;
                        if (speed < minScrapeSpeed * 10)
                            intensityMultiplier = speed / (minScrapeSpeed * 10);
                        scrapeLight.intensity = UnityEngine.Random.Range(0f, sparkLightIntensity * intensityMultiplier);
                    }
                    else
                    {
                        /*fragmentFx.transform.position = contactPoint;
                        fragmentFx.particleEmitter.maxEnergy = speed / 10;                          // Values determined
                        fragmentFx.particleEmitter.maxEmission = Mathf.Clamp((speed * 2), 0, 75);   // via experimentation.
                        fragmentFx.particleEmitter.Emit();
                        fragmentFx.particleEmitter.worldVelocity = -part.Rigidbody.velocity;
                        if (fragmentAnimator != null)
                        {
                            Color[] colors = dustAnimator.colorAnimation;
                            Color light = new Color(0.95f, 0.95f, 0.95f);
                            Color dark = new Color(0.05f, 0.05f, 0.05f);

                            colors[0] = Color.gray;
                            colors[1] = light;
                            colors[2] = dark;
                            colors[3] = light;
                            colors[4] = dark;
                            fragmentAnimator.colorAnimation = colors;
                        }*/
                    }
                }
                Color c = ColourManager.GetDustColour(colliderName);
                if (c != Color.clear)
                {
                    dustFx.transform.position = contactPoint;
                    dustFxParticleEmitter.maxEnergy = speed / 10;                          // Values determined
                    dustFxParticleEmitter.maxEmission = Mathf.Clamp((speed * 2), 0, 75);   // via experimentation.
                    dustFxParticleEmitter.Emit();
                    //dustFx.particleEmitter.worldVelocity = -part.Rigidbody.velocity;
                    // Set dust biome colour.
                    if (dustAnimator != null)
                    {
                        Color[] colors = dustAnimator.colorAnimation;
                        colors[0] = c;
                        colors[1] = c;
                        colors[2] = c;
                        colors[3] = c;
                        colors[4] = c;
                        dustAnimator.colorAnimation = colors;
                    }
                }
            }
            else
            {
                if (scrapeLight != null)
                    scrapeLight.enabled = false;
            }
        }

        private void DustImpact(float speed, Vector3 contactPoint, string colliderName)
        {
            Color c = ColourManager.GetDustColour(colliderName);
            if (c == Color.clear)
            c = ColourManager.GenericDustColour;
            dustFx.transform.position = contactPoint;
            dustFxParticleEmitter.maxEnergy = speed / 10;                          // Values determined
            dustFxParticleEmitter.maxEmission = Mathf.Clamp((speed * 2), 0, 75);   // via experimentation.
            dustFxParticleEmitter.Emit();
            // Set dust biome colour.
            if (dustAnimator != null)
            {
                Color[] colors = dustAnimator.colorAnimation;
                colors[0] = c;
                colors[1] = c;
                colors[2] = c;
                colors[3] = c;
                colors[4] = c;
                dustAnimator.colorAnimation = colors;
            }
        }

        private void ScrapeSound(FXGroup sound, float speed)
        {
            if (sound == null || sound.audio == null)
                return;
            if (speed > minScrapeSpeed)
            {
                if (!sound.audio.isPlaying)
                    sound.audio.Play();
                sound.audio.pitch = 1 + Mathf.Log(speed) / 5;

                if (speed < scrapeFadeSpeed)
                {
                    // Fade out at low speeds.
                    sound.audio.volume = speed / scrapeFadeSpeed * volume * GameSettings.SHIP_VOLUME;
                }
                else
                    sound.audio.volume = volume * GameSettings.SHIP_VOLUME;
            }
            else
                sound.audio.Stop();
        }
    }

#if DEBUG
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    class AutoStartup : UnityEngine.MonoBehaviour
    {
        public static bool first = true;
        public void Start()
        {
            //only do it on the first entry to the menu
            if (first)
            {
                first = false;
                HighLogic.SaveFolder = "test";
                var game = GamePersistence.LoadGame("persistent", HighLogic.SaveFolder, true, false);
                if (game != null && game.flightState != null && game.compatible)
                    FlightDriver.StartAndFocusVessel(game, game.flightState.activeVesselIdx);
                CheatOptions.InfinitePropellant = true;
                CheatOptions.InfiniteElectricity = true;
            }
        }
    }
#endif
}


//ScreenMessages.PostScreenMessage("Collider: " + c.collider + "\ngameObject: " + c.gameObject + "\nrigidbody: " + c.Rigidbody + "\ntransform: " + c.transform);
/*
    Collider		    gameObject		    rigidbody		transform
    runway_collider		runway_collider		""			runway_collider
    End09_Mesh		    End09_Mesh		    ""			End09_Mesh
    Section4_Mesh
    Section3_Mesh
    Section2_Mesh
    Section1_Mesh
    End27_Mesh
    Zn1232223233		Zn1232223233		""			Zn1232223233		
    Zn1232223332
    model_launchpad_ground_collider_v46
    Launch Pad
    launchpad_ramps
    Fuel Pipe
    Fuel Port
    launchpad_shoulders
    model_vab_exterior_crawlerway_collider_v46
    Zn1232223211
    Zn1232223210
    Zn1232223032
    Zn3001100000 - mountain top
    Zn2101022132 - Desert
    Zn2101022133
    Yp0333302322 - North pole
 * */