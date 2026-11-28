using System;
using System.IO;
using System.Text;

namespace BringRig2D;

/// <summary>
/// Starter .rig2d.json generators (biped, quadruped, creature, prop) ready for AI or artist customization.
/// </summary>
public static class RigTemplates
{
    public static int Run(string type, string name, string outPath)
    {
        string safeName = string.IsNullOrWhiteSpace(name) ? type : name.Trim();
        string json = type.ToLowerInvariant() switch
        {
            "biped" => BuildBipedTemplate(safeName),
            "quadruped" => BuildQuadrupedTemplate(safeName),
            "creature" => BuildCreatureTemplate(safeName),
            "prop" => BuildPropTemplate(safeName),
            _ => "",
        };

        if (string.IsNullOrEmpty(json))
        {
            Console.Error.WriteLine($"[BringRig2D] ERROR: Unknown template type '{type}'. Expected biped|quadruped|creature|prop.");
            return 2;
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outPath, json, new UTF8Encoding(false));
        Console.WriteLine($"[BringRig2D] Wrote '{type}' template rig '{safeName}' -> '{outPath}'.");
        return 0;
    }

    private static string BuildBipedTemplate(string name) => $$"""
{
  "name": "{{name}}",
  "pixelsPerUnit": 100,
  "origin": [0, 0],
  "size": [320, 420],
  "bones": [
    { "name": "root", "x": 0, "y": 0, "length": 0 },
    { "name": "hip", "parent": "root", "x": 0, "y": 150, "length": 40 },
    { "name": "torso", "parent": "hip", "x": 0, "y": 28, "rotation": 90, "length": 95 },
    { "name": "head", "parent": "torso", "x": 95, "y": 0, "rotation": 0, "length": 68 },
    { "name": "cape_0", "parent": "torso", "x": 85, "y": 18, "rotation": 175, "length": 60 },
    { "name": "cape_1", "parent": "cape_0", "x": 60, "y": 0, "rotation": 5, "length": 75 },
    { "name": "arm_back_upper", "parent": "torso", "x": 80, "y": -20, "rotation": -165, "length": 58 },
    { "name": "arm_back_lower", "parent": "arm_back_upper", "x": 58, "y": 0, "rotation": 18, "length": 54 },
    { "name": "arm_front_upper", "parent": "torso", "x": 80, "y": 20, "rotation": 165, "length": 58 },
    { "name": "arm_front_lower", "parent": "arm_front_upper", "x": 58, "y": 0, "rotation": 22, "length": 54 },
    { "name": "weapon", "parent": "arm_front_lower", "x": 52, "y": 0, "rotation": -35, "length": 110 },
    { "name": "thigh_back", "parent": "hip", "x": 14, "y": -6, "rotation": -86, "length": 72 },
    { "name": "shin_back", "parent": "thigh_back", "x": 72, "y": 0, "rotation": -4, "length": 72 },
    { "name": "thigh_front", "parent": "hip", "x": -14, "y": -6, "rotation": -94, "length": 72 },
    { "name": "shin_front", "parent": "thigh_front", "x": 72, "y": 0, "rotation": 4, "length": 72 },
    { "name": "foot_back_ik", "parent": "root", "x": 20, "y": 4, "length": 0 },
    { "name": "foot_front_ik", "parent": "root", "x": -20, "y": 4, "length": 0 }
  ],
  "slots": [
    { "name": "cape", "bone": "cape_0", "attachment": "cape_mesh" },
    { "name": "arm_back", "bone": "arm_back_upper", "attachment": "arm_back" },
    { "name": "leg_back", "bone": "thigh_back", "attachment": "leg_back" },
    { "name": "torso", "bone": "torso", "attachment": "torso" },
    { "name": "head", "bone": "head", "attachment": "head" },
    { "name": "leg_front", "bone": "thigh_front", "attachment": "leg_front" },
    { "name": "weapon", "bone": "weapon", "attachment": "weapon" },
    { "name": "arm_front", "bone": "arm_front_upper", "attachment": "arm_front" }
  ],
  "skins": {
    "default": {
      "cape": {
        "cape_mesh": {
          "type": "mesh",
          "image": "parts/cape",
          "x": 65, "y": 0, "rotation": -90, "width": 110, "height": 150,
          "autoMesh": { "density": 24, "outlineMargin": 2, "bones": ["cape_0", "cape_1"], "smoothIterations": 3 }
        }
      },
      "arm_back": { "arm_back": { "type": "region", "image": "parts/arm_back", "x": 45, "y": 0, "rotation": -90, "width": 44, "height": 115 } },
      "leg_back": { "leg_back": { "type": "region", "image": "parts/leg_back", "x": 65, "y": 0, "rotation": -90, "width": 48, "height": 148 } },
      "torso": { "torso": { "type": "region", "image": "parts/torso", "x": 48, "y": 0, "rotation": -90, "width": 96, "height": 116 } },
      "head": { "head": { "type": "region", "image": "parts/head", "x": 34, "y": 0, "rotation": -90, "width": 76, "height": 80 } },
      "leg_front": { "leg_front": { "type": "region", "image": "parts/leg_front", "x": 65, "y": 0, "rotation": -90, "width": 48, "height": 148 } },
      "weapon": { "weapon": { "type": "region", "image": "parts/weapon", "x": 55, "y": 0, "rotation": -90, "width": 48, "height": 140 } },
      "arm_front": { "arm_front": { "type": "region", "image": "parts/arm_front", "x": 45, "y": 0, "rotation": -90, "width": 44, "height": 115 } }
    }
  },
  "constraints": {
    "ik": [
      { "name": "leg_back_ik", "order": 0, "bones": ["thigh_back", "shin_back"], "target": "foot_back_ik", "mix": 1.0, "softness": 4.0, "bendDirection": -1 },
      { "name": "leg_front_ik", "order": 1, "bones": ["thigh_front", "shin_front"], "target": "foot_front_ik", "mix": 1.0, "softness": 4.0, "bendDirection": -1 }
    ],
    "spring": [
      { "name": "cape_spring", "order": 2, "bone": "cape_1", "inertia": 0.6, "strength": 70, "damping": 7, "mass": 1.0, "gravity": 140, "wind": 14, "maxAngle": 32, "mix": 1.0, "rotateScale": 1.0 }
    ]
  },
  "events": {
    "footstep": { "int": 0, "float": 1.0, "string": "step" }
  },
  "animations": {
    "idle": {
      "duration": 1.4,
      "bones": {
        "hip": {
          "translate": [
            { "time": 0.0, "x": 0, "y": 0, "curve": "easeInOutSine" },
            { "time": 0.7, "x": 0, "y": -5, "curve": "easeInOutSine" },
            { "time": 1.4, "x": 0, "y": 0 }
          ]
        },
        "torso": {
          "rotate": [
            { "time": 0.0, "value": 0, "curve": "easeInOutSine" },
            { "time": 0.7, "value": -2.5, "curve": "easeInOutSine" },
            { "time": 1.4, "value": 0 }
          ]
        },
        "head": {
          "rotate": [
            { "time": 0.0, "value": 0, "curve": "easeInOutSine" },
            { "time": 0.7, "value": 3.0, "curve": "easeInOutSine" },
            { "time": 1.4, "value": 0 }
          ]
        }
      }
    },
    "walk": {
      "duration": 0.8,
      "bones": {
        "hip": {
          "translate": [
            { "time": 0.0, "x": 0, "y": 0, "curve": "easeInOutSine" },
            { "time": 0.2, "x": 0, "y": 8, "curve": "easeInOutSine" },
            { "time": 0.4, "x": 0, "y": 0, "curve": "easeInOutSine" },
            { "time": 0.6, "x": 0, "y": 8, "curve": "easeInOutSine" },
            { "time": 0.8, "x": 0, "y": 0 }
          ]
        },
        "foot_front_ik": {
          "translate": [
            { "time": 0.0, "x": 38, "y": 0, "curve": "easeInOut" },
            { "time": 0.2, "x": 0, "y": 0, "curve": "easeInOut" },
            { "time": 0.4, "x": -38, "y": 0, "curve": "easeOut" },
            { "time": 0.6, "x": 0, "y": 24, "curve": "easeIn" },
            { "time": 0.8, "x": 38, "y": 0 }
          ]
        },
        "foot_back_ik": {
          "translate": [
            { "time": 0.0, "x": -38, "y": 0, "curve": "easeOut" },
            { "time": 0.2, "x": 0, "y": 24, "curve": "easeIn" },
            { "time": 0.4, "x": 38, "y": 0, "curve": "easeInOut" },
            { "time": 0.6, "x": 0, "y": 0, "curve": "easeInOut" },
            { "time": 0.8, "x": -38, "y": 0 }
          ]
        }
      },
      "events": [
        { "time": 0.0, "name": "footstep", "int": 0, "string": "front" },
        { "time": 0.4, "name": "footstep", "int": 1, "string": "back" }
      ]
    }
  }
}
""";

    private static string BuildQuadrupedTemplate(string name) => $$"""
{
  "name": "{{name}}",
  "pixelsPerUnit": 100,
  "origin": [0, 0],
  "size": [420, 280],
  "bones": [
    { "name": "root", "x": 0, "y": 0, "length": 0 },
    { "name": "body", "parent": "root", "x": 0, "y": 115, "rotation": 0, "length": 130 },
    { "name": "neck", "parent": "body", "x": 65, "y": 15, "rotation": 45, "length": 48 },
    { "name": "head", "parent": "neck", "x": 48, "y": 0, "rotation": -35, "length": 55 },
    { "name": "tail_0", "parent": "body", "x": -65, "y": 8, "rotation": 155, "length": 42 },
    { "name": "tail_1", "parent": "tail_0", "x": 42, "y": 0, "rotation": 10, "length": 44 },
    { "name": "leg_fl_u", "parent": "body", "x": 50, "y": -10, "rotation": -90, "length": 54 },
    { "name": "leg_fl_l", "parent": "leg_fl_u", "x": 54, "y": 0, "rotation": 0, "length": 52 },
    { "name": "leg_bl_u", "parent": "body", "x": -50, "y": -10, "rotation": -90, "length": 54 },
    { "name": "leg_bl_l", "parent": "leg_bl_u", "x": 54, "y": 0, "rotation": 0, "length": 52 },
    { "name": "paw_fl_ik", "parent": "root", "x": 50, "y": 4, "length": 0 },
    { "name": "paw_bl_ik", "parent": "root", "x": -50, "y": 4, "length": 0 }
  ],
  "slots": [
    { "name": "tail", "bone": "tail_0", "attachment": "tail" },
    { "name": "leg_bl", "bone": "leg_bl_u", "attachment": "leg_bl" },
    { "name": "body", "bone": "body", "attachment": "body" },
    { "name": "head", "bone": "head", "attachment": "head" },
    { "name": "leg_fl", "bone": "leg_fl_u", "attachment": "leg_fl" }
  ],
  "skins": {
    "default": {
      "tail": { "tail": { "type": "region", "image": "parts/tail", "x": 40, "y": 0, "width": 90, "height": 32 } },
      "leg_bl": { "leg_bl": { "type": "region", "image": "parts/leg_bl", "x": 50, "y": 0, "rotation": -90, "width": 38, "height": 110 } },
      "body": { "body": { "type": "region", "image": "parts/body", "x": 0, "y": 0, "width": 160, "height": 84 } },
      "head": { "head": { "type": "region", "image": "parts/head", "x": 24, "y": 0, "width": 84, "height": 68 } },
      "leg_fl": { "leg_fl": { "type": "region", "image": "parts/leg_fl", "x": 50, "y": 0, "rotation": -90, "width": 38, "height": 110 } }
    }
  },
  "constraints": {
    "ik": [
      { "name": "fl_ik", "order": 0, "bones": ["leg_fl_u", "leg_fl_l"], "target": "paw_fl_ik", "mix": 1.0, "softness": 3.0, "bendDirection": 1 },
      { "name": "bl_ik", "order": 1, "bones": ["leg_bl_u", "leg_bl_l"], "target": "paw_bl_ik", "mix": 1.0, "softness": 3.0, "bendDirection": -1 }
    ],
    "spring": [
      { "name": "tail_spring", "order": 2, "bone": "tail_1", "inertia": 0.7, "strength": 65, "damping": 6, "mass": 1.0, "gravity": 60, "wind": 10, "maxAngle": 40, "mix": 1.0, "rotateScale": 1.0 }
    ]
  },
  "animations": {
    "idle": {
      "duration": 1.2,
      "bones": {
        "body": {
          "translate": [
            { "time": 0.0, "x": 0, "y": 0, "curve": "easeInOutSine" },
            { "time": 0.6, "x": 0, "y": -4, "curve": "easeInOutSine" },
            { "time": 1.2, "x": 0, "y": 0 }
          ]
        }
      }
    }
  }
}
""";

    private static string BuildCreatureTemplate(string name) => $$"""
{
  "name": "{{name}}",
  "pixelsPerUnit": 100,
  "origin": [0, 0],
  "size": [320, 320],
  "bones": [
    { "name": "root", "x": 0, "y": 0, "length": 0 },
    { "name": "core", "parent": "root", "x": 0, "y": 140, "rotation": 90, "length": 80 },
    { "name": "wing_l", "parent": "core", "x": 45, "y": 30, "rotation": 45, "length": 95 },
    { "name": "wing_r", "parent": "core", "x": 45, "y": -30, "rotation": -45, "length": 95 },
    { "name": "tendril", "parent": "core", "x": -10, "y": 0, "rotation": 180, "length": 75 }
  ],
  "slots": [
    { "name": "wing_r", "bone": "wing_r", "attachment": "wing_r" },
    { "name": "tendril", "bone": "tendril", "attachment": "tendril" },
    { "name": "core", "bone": "core", "attachment": "core" },
    { "name": "wing_l", "bone": "wing_l", "attachment": "wing_l" }
  ],
  "skins": {
    "default": {
      "wing_r": { "wing_r": { "type": "region", "image": "parts/wing_r", "x": 48, "y": 0, "width": 110, "height": 72 } },
      "tendril": { "tendril": { "type": "mesh", "image": "parts/tendril", "x": 38, "y": 0, "rotation": -90, "width": 52, "height": 90, "autoMesh": { "density": 18, "bones": ["core", "tendril"] } } },
      "core": { "core": { "type": "region", "image": "parts/core", "x": 40, "y": 0, "rotation": -90, "width": 96, "height": 96 } },
      "wing_l": { "wing_l": { "type": "region", "image": "parts/wing_l", "x": 48, "y": 0, "width": 110, "height": 72 } }
    }
  },
  "constraints": {
    "spring": [
      { "name": "tendril_spring", "order": 0, "bone": "tendril", "inertia": 0.8, "strength": 55, "damping": 5, "mass": 1.0, "gravity": 100, "wind": 16, "maxAngle": 42, "mix": 1.0, "rotateScale": 1.0 }
    ]
  },
  "animations": {
    "idle": {
      "duration": 1.0,
      "bones": {
        "core": {
          "translate": [
            { "time": 0.0, "x": 0, "y": 0, "curve": "easeInOutSine" },
            { "time": 0.5, "x": 0, "y": 14, "curve": "easeInOutSine" },
            { "time": 1.0, "x": 0, "y": 0 }
          ]
        },
        "wing_l": {
          "rotate": [
            { "time": 0.0, "value": -12, "curve": "easeInOutSine" },
            { "time": 0.5, "value": 24, "curve": "easeInOutSine" },
            { "time": 1.0, "value": -12 }
          ]
        },
        "wing_r": {
          "rotate": [
            { "time": 0.0, "value": 12, "curve": "easeInOutSine" },
            { "time": 0.5, "value": -24, "curve": "easeInOutSine" },
            { "time": 1.0, "value": 12 }
          ]
        }
      }
    }
  }
}
""";

    private static string BuildPropTemplate(string name) => $$"""
{
  "name": "{{name}}",
  "pixelsPerUnit": 100,
  "origin": [0, 0],
  "size": [256, 256],
  "bones": [
    { "name": "root", "x": 0, "y": 0, "length": 0 },
    { "name": "base", "parent": "root", "x": 0, "y": 0, "rotation": 90, "length": 64 },
    { "name": "top", "parent": "base", "x": 64, "y": 0, "rotation": 0, "length": 64 }
  ],
  "slots": [
    { "name": "base", "bone": "base", "attachment": "base" },
    { "name": "top", "bone": "top", "attachment": "top" }
  ],
  "skins": {
    "default": {
      "base": { "base": { "type": "region", "image": "parts/base", "x": 32, "y": 0, "rotation": -90, "width": 96, "height": 72 } },
      "top": { "top": { "type": "region", "image": "parts/top", "x": 32, "y": 0, "rotation": -90, "width": 96, "height": 72 } }
    }
  },
  "animations": {
    "idle": {
      "duration": 1.0,
      "bones": {
        "top": {
          "rotate": [
            { "time": 0.0, "value": 0, "curve": "easeInOutSine" },
            { "time": 0.5, "value": 6, "curve": "easeInOutSine" },
            { "time": 1.0, "value": 0 }
          ]
        }
      }
    }
  }
}
""";
}
