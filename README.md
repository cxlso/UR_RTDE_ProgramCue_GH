# UR RTDE Program Cue Loop in Grasshopper
###### RTDE-based UR program cue looping in Grasshopper with Robots plugin. Features 2 workflows using runtime_state monitoring to upload programs sequentially: (1) 3D printing with large toolpath splitting and (2) milling with automated rail repositioning.

## RTDE Client Script

This repository includes a **Grasshopper C# RTDE client** used only to **read live feedback** from a Universal Robot — *not to upload or run programs*. Program sending is handled through the standard **Robots plugin remote interface**, while this client runs in parallel to monitor machine state.

> **I am not the author of these scripts.**
> I found them online some time ago and cannot relocate the original source.
> I believe they were written by [**Visose**](https://github.com/visose), the creator of the Robots plugin.
> I only updated them to work inside the newer Grasshopper C# environment

The script exposes functions implemented in the Robots plugin [RTDE layer](https://github.com/visose/Robots/blob/master/src/Robots/Remotes/URRealTimeDataExchange.cs).

It connects to the robot, reads the `URRealTimeDataExchange` outputs, and makes them available to Grasshopper for tasks such as:

* tracking `runtime_state` to cue next program
* monitoring `script_control_line`
* providing real-time `actual_TCP_pose`

### DLL Linking Note

Since Rhino/Grasshopper 8 changed the C# component behavior, the script requires manual reference to the `Robots.dll` library.

Example path (adjust to your install):

```
C:\Users\[USERNAME]\AppData\Roaming\McNeel\Rhinoceros\packages\8.0\Robots\1.9.0\Robots.dll
```

### Files Included

📂 `CSharp/URTDE_Client.cs`
Reads RTDE outputs live and returns values + logs.

📂 `CSharp/URTDE_Variables.cs`
Lists all available RTDE variable names from the Robots plugin.

<details>
<summary><strong>Click to expand list ↓</strong></summary>

```
 0. timestamp
 1. target_q
 2. target_qd
 3. target_qdd
 4. target_current
 5. target_moment
 6. actual_q
 7. actual_qd
 8. actual_current
 9. joint_control_output
10. actual_TCP_pose
11. actual_TCP_speed
12. actual_TCP_force
13. target_TCP_pose
14. target_TCP_speed
15. actual_digital_input_bits
16. joint_temperatures
17. actual_execution_time
18. robot_mode
19. joint_mode
20. safety_mode
21. safety_status
22. actual_tool_accelerometer
23. speed_scaling
24. target_speed_fraction
25. actual_momentum
26. actual_main_voltage
27. actual_robot_voltage
28. actual_robot_current
29. actual_joint_voltage
30. actual_digital_output_bits
31. runtime_state
32. elbow_position
33. elbow_velocity
34. robot_status_bits
35. safety_status_bits
36. analog_io_types
37. standard_analog_input0
38. standard_analog_input1
39. standard_analog_output0
40. standard_analog_output1
41. io_current
42. euromap67_input_bits
43. euromap67_output_bits
44. euromap67_24V_voltage
45. euromap67_24V_current
46. tool_mode
47. tool_analog_input_types
48. tool_analog_input0
49. tool_analog_input1
50. tool_output_voltage
51. tool_output_current
52. tool_temperature
53. tcp_force_scalar
54. output_bit_registers0_to_31
55. output_bit_registers32_to_63
56. output_bit_register_X (X = [64..127])
57. output_int_register_X (X = [0..47])
58. — reserved / not used —
59. — reserved / not used —
60. output_double_register_X (X = [0..47])
61. — reserved / not used —
62. — reserved / not used —
63. input_bit_registers0_to_31
64. input_bit_registers32_to_63
65. input_bit_register_X (X = [64..127])
66. input_int_register_X (X = [0..48])
67. — reserved / not used —
68. — reserved / not used —
69. input_double_register_X (X = [0..48])
70. — reserved / not used —
71. — reserved / not used —
72. tool_output_mode
73. tool_digital_output0_mode
74. tool_digital_output1_mode
75. payload
76. payload_cog
77. payload_inertia
78. script_control_line
79. ft_raw_wrench
```

</details>


## Photos & Videos

https://github.com/user-attachments/assets/fd91012d-67a6-480a-8aae-c6483b480235

## Contributing

Contributions and suggestions are welcome.
Please submit a pull request or open an issue to discuss improvements.

## License

[![CC BY-NC-SA 4.0][cc-by-nc-sa-shield]][cc-by-nc-sa]

This work is licensed under a [Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International License][cc-by-nc-sa].

[![CC BY-NC-SA 4.0][cc-by-nc-sa-image]][cc-by-nc-sa]

## Acknowledgements


Project developped in collaboration with Sujay at [FIU RDFlab](https://carta.fiu.edu/roboticslab/).


<!-- Shields and link definitions -->

[cc-by-nc-sa]: http://creativecommons.org/licenses/by-nc-sa/4.0/
[cc-by-nc-sa-image]: https://licensebuttons.net/l/by-nc-sa/4.0/88x31.png
[cc-by-nc-sa-shield]: https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-lightgrey.svg
