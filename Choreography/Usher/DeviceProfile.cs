using System;

namespace Choreography.Usher
{
    // Identificacion humana del dispositivo Stage que esta pidiendo entrar a la red.
    // Lo ve el operador en ContactSecret para decidir si aprueba o no. No es parte de la
    // identidad criptografica (esa la lleva StagePublicKey), solo informacion UX
    // para el approval manual (D4).
    public sealed class DeviceProfile
    {
        public string Name { get; }
        public string Fingerprint { get; }

        public DeviceProfile(string name, string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));
            if (string.IsNullOrWhiteSpace(fingerprint)) throw new ArgumentNullException(nameof(fingerprint));
            Name = name;
            Fingerprint = fingerprint;
        }
    }
}
